using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace NetCasperSDK.JsonRpc
{
    public class RpcResponse<TRpcResult>
    {
        [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; }

        [JsonPropertyName("id")] public uint Id { get; init; }

        [JsonPropertyName("result")] public JsonElement Result { get; init; }

        [JsonPropertyName("error")] public RpcError Error { get; init; }

        public TRpcResult Parse()
        {
            return JsonSerializer.Deserialize<TRpcResult>(this.Result.GetRawText());
        }
    }
}