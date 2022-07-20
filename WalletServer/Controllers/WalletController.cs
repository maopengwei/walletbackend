using System.Text.Json;
using System.Text.Json.Serialization;
using chia.dotnet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NodeDBSyncer.Helpers;
using Prometheus;
using WalletServer.Helpers;

namespace WalletServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WalletController : ControllerBase
    {
        private readonly ILogger<WalletController> logger;
        private readonly IMemoryCache memoryCache;
        private readonly DataAccess dataAccess;
        private readonly PushLogHelper pushLogHelper;
        private readonly OnlineCounter onlineCounter;
        private readonly AppSettings appSettings;
        private readonly HttpRpcClient rpcClient;
        private readonly FullNodeProxy client;

        private static readonly Counter RequestRecordCount = Metrics.CreateCounter("request_record_total", "Number of record request.");
        private static readonly Counter PushTxCount = Metrics.CreateCounter("push_tx_total", "Number of pushtx request.");
        private static readonly Counter PushTxSuccessCount = Metrics.CreateCounter("push_tx_success_total", "Number of successful pushtx request.");
        private static readonly Counter RequestPuzzleCount = Metrics.CreateCounter("request_puzzle_total", "Number of puzzle request.");
        private static readonly Counter RequestCoinSolutionCount = Metrics.CreateCounter("request_coin_solution_total", "Number of CoinSolution request.");

        public WalletController(
            ILogger<WalletController> logger,
            IMemoryCache memoryCache,
            DataAccess dataAccess,
            PushLogHelper pushLogHelper,
            OnlineCounter onlineCounter,
            IOptions<AppSettings> appSettings)
        {
            this.logger = logger;
            this.memoryCache = memoryCache;
            this.dataAccess = dataAccess;
            this.pushLogHelper = pushLogHelper;
            this.onlineCounter = onlineCounter;
            this.appSettings = appSettings.Value;
            // command: redir :8666 :8555
            var path = this.appSettings.Path ?? "";
            var endpoint = new EndpointInfo
            {
                CertPath = path + "private_full_node.crt",
                KeyPath = path + "private_full_node.key",
                Uri = new Uri($"https://{this.appSettings.Host}:{this.appSettings.Port}/"),
            };
            this.rpcClient = new HttpRpcClient(endpoint);
            this.client = new FullNodeProxy(this.rpcClient, "client") { MaxRetries = 2 };
        }

        public record GetRecordsRequest(
            string[] puzzleHashes,
            long? startHeight = null,
            [property: Obsolete("don't need end height to restrict, as currently is using database instead of api")] ulong? endHeight = null,
            long? pageStart = null,
            int? pageLength = null,
            bool includeSpentCoins = false,
            bool hint = false);
        public record GetRecordsResponse(long peekHeight, CoinRecordInfo[] coins);
        public record CoinRecordInfo(string puzzleHash, CoinRecord[] records, long balance, FullBalanceInfo balanceInfo);

        private const int MaxCoinCount = 100;

        [HttpPost("records")]
        public async Task<ActionResult> GetRecords(GetRecordsRequest request)
        {
            if (request is null || request.puzzleHashes is null) return BadRequest("Invalid request");
            if (request.puzzleHashes.Length > 300)
                return BadRequest("Valid puzzle hash number per request is 300");

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.onlineCounter.Renew(remoteIpAddress, request.puzzleHashes[0], request.puzzleHashes.Length);
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request {request.puzzleHashes.FirstOrDefault()}"
                + $"[{request.puzzleHashes.Length }], includeSpent = {request.includeSpentCoins}");

            RequestRecordCount.Inc();
            var peak = await this.dataAccess.GetPeakHeight();

            var infos = new List<CoinRecordInfo>();
            foreach (var hash in request.puzzleHashes)
            {
                var balance = await this.dataAccess.GetBalance(hash);
                var coinRecords = await this.dataAccess.GetCoins(
                    hash,
                    request.includeSpentCoins,
                    GetCoinOrder.AnyIndexDesc,
                    request.hint ? GetCoinMethod.Hint : GetCoinMethod.PuzzleHash,
                    request.startHeight,
                    request.pageStart,
                    request.pageLength);
                infos.Add(new CoinRecordInfo(hash, coinRecords, balance.Amount, balance));
            }

            return Ok(new GetRecordsResponse(peak, infos.ToArray()));
        }

        public record PushTxRequest(SpendBundleReq? bundle);
        public record SpendBundleReq
        (
            [property: JsonPropertyName("aggregated_signature")] string AggregatedSignature,
            [property: JsonPropertyName("coin_spends")] CoinSpendReq[]? CoinSpends
        );
        public record CoinSpendReq
        (
            [property: JsonPropertyName("coin")] CoinItemReq? Coin,
            [property: JsonPropertyName("puzzle_reveal")] string PuzzleReveal,
            [property: JsonPropertyName("solution")] string Solution
        );
        public record CoinItemReq
        (
            [property: JsonPropertyName("amount")] ulong Amount,
            [property: JsonPropertyName("parent_coin_info")] string ParentCoinInfo,
            [property: JsonPropertyName("puzzle_hash")] string PuzzleHash
        );

        [HttpPost("pushtx")]
        public async Task<ActionResult> PushTx(PushTxRequest request)
        {
            if (request?.bundle?.CoinSpends == null) return BadRequest("Invalid request");
            PushTxCount.Inc();

            var bundle = new SpendBundle
            {
                AggregatedSignature = request.bundle.AggregatedSignature,
                CoinSpends = request.bundle.CoinSpends
                    .Select(cs => new CoinSpend
                    {
                        PuzzleReveal = cs.PuzzleReveal,
                        Solution = cs.Solution,
                        Coin = new Coin
                        {
                            Amount = cs?.Coin?.Amount ?? 0,
                            ParentCoinInfo = cs?.Coin?.ParentCoinInfo ?? throw new Exception(""),
                            PuzzleHash = cs?.Coin?.PuzzleHash ?? throw new Exception(""),
                        },
                    })
                    .ToList(),
            };

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} pushtx using coins[{request.bundle.CoinSpends.Length}]");
            var txid = (string?)null;
            var error = (string?)null;
            var status = 0;

            try
            {
                var result = await this.client.PushTx(bundle);

                if (!result)
                {
                    this.logger.LogWarning($"[{DateTime.UtcNow.ToShortTimeString()}]push tx failed\n============\n{JsonSerializer.Serialize(result)}\n============\n{JsonSerializer.Serialize(bundle)}");
                    status = 2;
                }
                else
                {
                    PushTxSuccessCount.Inc();
                    status = 1;
                }

                return Ok(new { success = result });
            }
            catch (ResponseException re)
            {
                this.logger.LogWarning($"[{DateTime.UtcNow.ToShortTimeString()}]push tx failed\n============\n{re.Message}\n============\n{JsonSerializer.Serialize(bundle)}");
                status = 3;
                error = re.Message;
                return BadRequest(new { success = false, error = re.Message });
            }
            finally
            {
                try
                {
                    await this.pushLogHelper.LogPushes(new PushLogEntity(
                        JsonSerializer.SerializeToUtf8Bytes(request.bundle).Compress(),
                        System.Net.IPAddress.Parse(remoteIpAddress),
                        txid,
                        status,
                        DateTime.UtcNow,
                        error));
                }
                catch (Exception ex)
                {
                    // ignore all exceptions
                    this.logger.LogWarning(ex, $"push log failed");
                }
            }
        }

        public record GetParentPuzzleRequest(string parentCoinId);
        public record GetParentPuzzleResponse(string parentCoinId, ulong amount, string parentParentCoinId, string puzzleReveal);

        [HttpPost("get-puzzle")]
        public async Task<ActionResult> GetParentPuzzle(GetParentPuzzleRequest request)
        {
            if (request == null || request.parentCoinId == null) return BadRequest("Invalid request");
            RequestPuzzleCount.Inc();

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogDebug($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request puzzle {request.parentCoinId}");

            var parentCoin = await this.client.GetCoinRecordByName(request.parentCoinId);
            if (!parentCoin.Spent) return BadRequest("Coin not spend yet.");

            var spend = await this.client.GetPuzzleAndSolution(request.parentCoinId, parentCoin.SpentBlockIndex);
            if (string.IsNullOrEmpty(spend.PuzzleReveal))
            {
                this.logger.LogWarning($"failed to get puzzle for {parentCoin.Coin.ParentCoinInfo} on {parentCoin.ConfirmedBlockIndex}");
                return BadRequest("Failed to get coin.");
            }

            return Ok(new GetParentPuzzleResponse(request.parentCoinId, parentCoin.Coin.Amount, parentCoin.Coin.ParentCoinInfo, spend.PuzzleReveal));
        }

        public record GetCoinSolutionRequest(string coinId);
        public record GetCoinSolutionResponse(CoinSpendReq CoinSpend);

        [HttpPost("get-coin-solution")]
        public async Task<ActionResult> GetCoinSolution(GetCoinSolutionRequest request)
        {
            if (request == null || request.coinId == null) return BadRequest("Invalid request");
            RequestCoinSolutionCount.Inc();

            var remoteIpAddress = this.HttpContext.GetRealIp();
            this.logger.LogInformation($"[{DateTime.UtcNow.ToShortTimeString()}]From {remoteIpAddress} request puzzle[debug] {request.coinId}");

            var thisRecord = await this.client.GetCoinRecordByName(request.coinId);
            if (!thisRecord.Spent)
            {
                var c = thisRecord.Coin;
                return Ok(new GetCoinSolutionResponse(new CoinSpendReq(
                    new CoinItemReq(c.Amount, c.ParentCoinInfo, c.PuzzleHash), string.Empty, string.Empty)));
            }

            var cs = await this.client.GetPuzzleAndSolution(request.coinId, thisRecord.SpentBlockIndex);
            if (string.IsNullOrEmpty(cs.PuzzleReveal) || string.IsNullOrEmpty(cs.Solution))
            {
                this.logger.LogWarning($"failed to get puzzle for {thisRecord.Coin.ParentCoinInfo} on {thisRecord.ConfirmedBlockIndex}");
                return BadRequest("Failed to get coin.");
            }

            return Ok(new GetCoinSolutionResponse(new CoinSpendReq(
                new CoinItemReq(cs.Coin.Amount, cs.Coin.ParentCoinInfo, cs.Coin.PuzzleHash), cs.PuzzleReveal, cs.Solution)));
        }

        public record GetNetworkInfoResponse(string name, string prefix);

        [HttpGet("network")]
        public async Task<ActionResult> GetNetworkInfo()
        {
            if (!this.memoryCache.TryGetValue(nameof(GetNetworkInfo), out GetNetworkInfoResponse cacheInfo))
            {
                var (name, prefix) = await this.client.GetNetworkInfo();
                cacheInfo = new GetNetworkInfoResponse(name, prefix);

                var cacheEntryOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromMinutes(10));

                this.memoryCache.Set(nameof(GetNetworkInfo), cacheInfo, cacheEntryOptions);
            }

            return Ok(cacheInfo);
        }
    }
}