using System.Text.Json.Serialization;

namespace NetCasperSDK.Types
{
    /// <summary>
    /// The summary of an era
    /// </summary>
    public class EraSummary
    {
        /// <summary>
        /// The block hash
        /// </summary>
        [JsonPropertyName("block_hash")]
        public string BlockHash { get; init; }
        
        /// <summary>
        /// The Era Id
        /// </summary>
        [JsonPropertyName("era_id")]
        public ulong EraId { get; init; }
        
        /// <summary>
        /// The merkle proof.
        /// </summary>
        [JsonPropertyName("merkle_proof")]
        public string MerkleProof { get; init; }
        
        /// <summary>
        /// Hex-encoded hash of the state root.
        /// </summary>
        [JsonPropertyName("state_root_hash")]
        public string StateRootHash { get; init; }
    }
}