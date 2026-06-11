namespace Foliant.Models;

/// <summary>One downloadable model asset.</summary>
/// <param name="Id">Stable identifier (also the catalog lookup key).</param>
/// <param name="FileName">Local file name inside the cache/models directory.</param>
/// <param name="Url">Direct download URL (Hugging Face resolve link).</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file contents.</param>
/// <param name="SizeBytes">Expected size, for progress reporting and sanity checks.</param>
public sealed record ModelAsset(
    string Id,
    string FileName,
    string Url,
    string Sha256,
    long SizeBytes);
