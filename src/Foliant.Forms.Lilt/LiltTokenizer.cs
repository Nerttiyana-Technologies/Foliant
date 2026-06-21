using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Foliant.Forms.Lilt;

/// <summary>
/// RoBERTa byte-level BPE tokenizer for the LiLT form-K-V model, hand-rolled to match HuggingFace exactly
/// (Microsoft.ML.Tokenizers 2.0 has no byte-level / add_prefix_space pre-tokenizer, so its IDs diverged).
/// Loaded from the model's own <c>vocab.json</c> + <c>merges.txt</c> (exported alongside <c>model.onnx</c>),
/// it reproduces the pipeline declared in the model's tokenizer.json: a single ByteLevel pre-tokenizer with
/// <c>add_prefix_space=true</c>, <c>use_regex=true</c> (GPT-2 split regex), bytes→'Ġ…' mapping, then BPE
/// merges ranked by merges.txt. Verified against HuggingFace by <c>LiltTokenizerParityTests</c> — do not
/// build featurization on top of this until parity holds.
/// </summary>
public sealed class LiltTokenizer
{
    // GPT-2 / RoBERTa pre-tokenization regex (use_regex=true), identical to HF's ByteLevel splitter.
    private static readonly Regex Gpt2Split = new(
        @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    private static readonly IReadOnlyDictionary<byte, char> ByteToChar = BuildByteToChar();

    private readonly Dictionary<string, int> _vocab;            // byte-level token string ("ĠName") -> id
    private readonly Dictionary<(string, string), int> _ranks;  // merge pair -> rank (lower merges first)

    private LiltTokenizer(Dictionary<string, int> vocab, Dictionary<(string, string), int> ranks)
    {
        _vocab = vocab;
        _ranks = ranks;
    }

    /// <summary>Loads the tokenizer from a directory containing <c>vocab.json</c> and <c>merges.txt</c>.</summary>
    public static LiltTokenizer Load(string modelDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDir);
        string vocabPath = Path.Combine(modelDir, "vocab.json");
        string mergesPath = Path.Combine(modelDir, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
            throw new FileNotFoundException($"RoBERTa vocab.json / merges.txt not found under '{modelDir}'.");

        using Stream vs = File.OpenRead(vocabPath);
        var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vs)
            ?? throw new InvalidOperationException($"Could not parse '{vocabPath}'.");

        var ranks = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (string line in File.ReadLines(mergesPath))
        {
            if (line.Length == 0 || line.StartsWith("#version", StringComparison.Ordinal)) continue;
            int sp = line.IndexOf(' ');
            if (sp <= 0 || sp >= line.Length - 1) continue;
            ranks[(line[..sp], line[(sp + 1)..])] = rank++;
        }

        return new LiltTokenizer(vocab, ranks);
    }

    /// <summary>Encodes raw text to token IDs (no special tokens). Mirrors HF whole-string encoding.</summary>
    public IReadOnlyList<int> EncodeToIds(string text) => Encode(text);

    /// <summary>Encodes one already-split word to its sub-word token IDs. Featurization propagates the
    /// word's box onto each of these sub-words (the LiLT bbox input is per-token). Matches HF's
    /// is_split_into_words=True + add_prefix_space behaviour (each word is treated as space-preceded).</summary>
    public IReadOnlyList<int> EncodeWord(string word) => Encode(word);

    private List<int> Encode(string text)
    {
        var ids = new List<int>();
        if (string.IsNullOrEmpty(text)) return ids;

        // add_prefix_space=true: ensure the leading word is space-preceded (so it gets the 'Ġ' marker),
        // but never double a space already present — this is exactly HF's behaviour.
        if (!text.StartsWith(' ')) text = " " + text;

        foreach (Match m in Gpt2Split.Matches(text))
        {
            // Byte-level map: UTF-8 bytes of the piece -> GPT-2 byte-to-unicode chars (space -> 'Ġ').
            var sb = new StringBuilder(m.Value.Length);
            foreach (byte b in Encoding.UTF8.GetBytes(m.Value)) sb.Append(ByteToChar[b]);

            foreach (string tok in BpeMerge(sb.ToString()))
            {
                if (_vocab.TryGetValue(tok, out int id)) ids.Add(id);
                // Byte-level vocab contains every single byte-char, so a fully-merged-down token always
                // resolves; a miss would only happen on a corrupt vocab, which we surface loudly.
                else throw new InvalidOperationException($"Byte-level token '{tok}' missing from vocab.");
            }
        }
        return ids;
    }

    // Standard GPT-2 BPE: repeatedly merge the adjacent pair with the lowest merge rank until none remain.
    private List<string> BpeMerge(string token)
    {
        var word = new List<string>(token.Length);
        foreach (char c in token) word.Add(c.ToString());
        if (word.Count < 2) return word;

        while (true)
        {
            int bestRank = int.MaxValue, bestIdx = -1;
            for (int i = 0; i < word.Count - 1; i++)
                if (_ranks.TryGetValue((word[i], word[i + 1]), out int r) && r < bestRank)
                {
                    bestRank = r;
                    bestIdx = i;
                }
            if (bestIdx < 0) break;

            string a = word[bestIdx], b = word[bestIdx + 1], merged = a + b;
            var next = new List<string>(word.Count);
            for (int i = 0; i < word.Count;)
            {
                if (i < word.Count - 1 && word[i] == a && word[i + 1] == b) { next.Add(merged); i += 2; }
                else { next.Add(word[i]); i++; }
            }
            word = next;
            if (word.Count < 2) break;
        }
        return word;
    }

    // GPT-2 byte-to-unicode table: maps each of the 256 bytes to a printable BMP char; non-printable bytes
    // (incl. space 0x20 -> U+0120 'Ġ') map above U+0100 so they survive as visible tokens.
    private static Dictionary<byte, char> BuildByteToChar()
    {
        var bs = new List<int>();
        for (int i = '!'; i <= '~'; i++) bs.Add(i);          // 33..126
        for (int i = 0xA1; i <= 0xAC; i++) bs.Add(i);        // 161..172
        for (int i = 0xAE; i <= 0xFF; i++) bs.Add(i);        // 174..255

        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++)
            if (!bs.Contains(b)) { bs.Add(b); cs.Add(256 + n); n++; }

        var map = new Dictionary<byte, char>(256);
        for (int i = 0; i < bs.Count; i++) map[(byte)bs[i]] = (char)cs[i];
        return map;
    }
}
