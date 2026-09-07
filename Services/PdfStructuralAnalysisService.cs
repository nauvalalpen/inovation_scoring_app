using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace DocumentAssessmentSystem3W1P.Services;

// ---------------------------------------------------------------------------
// Model — hasil analisis struktural PDF
// ---------------------------------------------------------------------------

/// <summary>
/// Statistik per halaman hasil ekstraksi deterministik dari PDF.
/// </summary>
public sealed class PageStat
{
    /// <summary>Nomor halaman (1-indexed).</summary>
    public int Page { get; init; }

    /// <summary>
    /// Jumlah karakter teks setelah normalisasi whitespace (collapse + trim).
    /// Angka EKSAK dari teks yang benar-benar terekstrak PdfPig, bukan estimasi visual.
    /// </summary>
    public int CharacterCount { get; init; }

    /// <summary>
    /// True jika halaman memiliki setidaknya satu elemen gambar XObject.
    /// </summary>
    public bool HasVisualContent { get; init; }

    /// <summary>
    /// True hanya jika CharacterCount == 0 DAN HasVisualContent == false.
    /// Halaman yang hanya berisi gambar tanpa teks bukan halaman blank.
    /// </summary>
    public bool IsBlank => CharacterCount == 0 && !HasVisualContent;
}

/// <summary>
/// Pasangan halaman yang terdeteksi memiliki kemiripan teks tinggi secara
/// algoritmik (kandidat redundansi). Gemini melakukan judgment semantik
/// apakah ini benar-benar redundansi mencurigakan.
/// </summary>
public sealed class RedundancyCandidate
{
    /// <summary>Nomor halaman pertama (1-indexed).</summary>
    public int PageA { get; init; }

    /// <summary>Nomor halaman kedua (1-indexed).</summary>
    public int PageB { get; init; }

    /// <summary>
    /// Jaccard similarity atas word-shingles n=5, rentang [0.0, 1.0].
    /// Skor tinggi = konten sangat mirip meski urutan kata/kalimat diubah.
    /// </summary>
    public double SimilarityScore { get; init; }

    /// <summary>
    /// Cuplikan singkat (maks 200 karakter) dari teks halaman A setelah
    /// normalisasi, sebagai konteks untuk judgment Gemini.
    /// </summary>
    public string MatchedExcerpt { get; init; } = string.Empty;
}

/// <summary>
/// Hasil keseluruhan analisis struktural PDF secara deterministik.
/// </summary>
public sealed class PdfStructuralAnalysis
{
    public int TotalPages { get; init; }
    public List<PageStat> Pages { get; init; } = [];
    public List<RedundancyCandidate> RedundancyCandidates { get; init; } = [];
}

// ---------------------------------------------------------------------------
// Service
// ---------------------------------------------------------------------------

/// <summary>
/// Menganalisis PDF secara deterministik (tanpa LLM) menggunakan PdfPig:
/// <list type="bullet">
///   <item>Menghitung jumlah karakter teks per halaman secara eksak.</item>
///   <item>Mendeteksi keberadaan elemen visual (gambar XObject).</item>
///   <item>Mendeteksi kandidat redundansi near-duplicate via Jaccard similarity
///         atas word-shingles n=5, threshold &gt;= 0.65.</item>
/// </list>
/// Service ini stateless sehingga aman di-register sebagai singleton.
/// </summary>
public sealed class PdfStructuralAnalysisService
{
    // Threshold similarity untuk menandai pasangan halaman sebagai kandidat redundansi.
    private const double SimilarityThreshold = 0.65;

    // Ukuran shingle (n-gram kata) untuk Jaccard similarity.
    private const int ShingleSize = 5;

    // Panjang maksimum cuplikan yang dikirim ke Gemini.
    private const int MaxExcerptLength = 200;

    // Minimum karakter teks per halaman (setelah filter header/footer) agar
    // halaman ikut dalam perbandingan redundansi. Halaman terlalu pendek
    // menghasilkan false positive.
    private const int MinCharsForRedundancyCheck = 80;

    // Regex dikompilasi sekali saat aplikasi start (singleton).
    private static readonly Regex WhitespaceRegex =
        new(@"\s+", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Analisis utama. Membuka PDF SATU KALI, mengekstrak semua data,
    /// lalu menghitung kandidat redundansi.
    /// </summary>
    public PdfStructuralAnalysis Analyze(byte[] pdfBytes)
    {
        var pageStats = new List<PageStat>();
        var rawNormalizedTexts = new List<string>(); // teks per halaman sebelum filter header/footer

        using (var document = PdfDocument.Open(pdfBytes))
        {
            foreach (var page in document.GetPages())
            {
                var rawText = page.Text ?? string.Empty;
                var normalized = NormalizeWhitespace(rawText);

                pageStats.Add(new PageStat
                {
                    Page = page.Number,
                    CharacterCount = normalized.Length,
                    HasVisualContent = page.GetImages().Any(),
                });

                rawNormalizedTexts.Add(normalized);
            }
        }

        // Filter baris universal (header/footer/watermark) sebelum membandingkan
        var filteredTexts = RemoveUniversalLines(rawNormalizedTexts);

        // Hitung kandidat redundansi secara pairwise
        var candidates = DetectRedundancyCandidates(pageStats, filteredTexts);

        return new PdfStructuralAnalysis
        {
            TotalPages = pageStats.Count,
            Pages = pageStats,
            RedundancyCandidates = candidates,
        };
    }

    // -----------------------------------------------------------------------
    // Deteksi kandidat redundansi
    // -----------------------------------------------------------------------

    private List<RedundancyCandidate> DetectRedundancyCandidates(
        List<PageStat> pages,
        List<string> filteredTexts)
    {
        var candidates = new List<RedundancyCandidate>();

        // Bangun shingle set untuk setiap halaman yang cukup panjang
        var shinglingSets = new HashSet<string>?[filteredTexts.Count];
        for (var i = 0; i < filteredTexts.Count; i++)
        {
            if (filteredTexts[i].Length >= MinCharsForRedundancyCheck)
            {
                shinglingSets[i] = BuildShingles(filteredTexts[i], ShingleSize);
            }
        }

        // Perbandingan pairwise O(n²) — wajar untuk PDF yang biasanya < 200 halaman
        for (var i = 0; i < shinglingSets.Length - 1; i++)
        {
            if (shinglingSets[i] is null) continue;

            for (var j = i + 1; j < shinglingSets.Length; j++)
            {
                if (shinglingSets[j] is null) continue;

                var score = JaccardSimilarity(shinglingSets[i]!, shinglingSets[j]!);
                if (score >= SimilarityThreshold)
                {
                    candidates.Add(new RedundancyCandidate
                    {
                        PageA = pages[i].Page,
                        PageB = pages[j].Page,
                        SimilarityScore = Math.Round(score, 3),
                        MatchedExcerpt = TruncateExcerpt(filteredTexts[i], MaxExcerptLength),
                    });
                }
            }
        }

        // Urutkan dari kemiripan tertinggi ke terendah
        candidates.Sort((a, b) => b.SimilarityScore.CompareTo(a.SimilarityScore));

        return candidates;
    }

    // -----------------------------------------------------------------------
    // Filter baris universal (header/footer/watermark)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Buang baris yang muncul identik di mayoritas halaman (&gt;= 70% dari halaman
    /// yang punya teks). Tujuan: menghilangkan header/footer/watermark standar
    /// agar tidak terhitung sebagai kesamaan semu antar halaman.
    /// </summary>
    private static List<string> RemoveUniversalLines(List<string> normalizedTexts)
    {
        var totalPagesWithText = normalizedTexts.Count(t => t.Length > 0);
        if (totalPagesWithText < 3)
        {
            // Dokumen terlalu pendek — tidak ada yang perlu difilter
            return normalizedTexts;
        }

        // Hitung frekuensi tiap baris unik di seluruh dokumen
        var lineFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var text in normalizedTexts)
        {
            var seenOnThisPage = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in SplitLines(text))
            {
                if (seenOnThisPage.Add(line))
                {
                    lineFrequency.TryGetValue(line, out var count);
                    lineFrequency[line] = count + 1;
                }
            }
        }

        // Threshold: >= 70% halaman ber-teks → baris dianggap universal
        var threshold = Math.Max(2, (int)Math.Ceiling(totalPagesWithText * 0.70));
        var universalLines = new HashSet<string>(
            lineFrequency.Where(kv => kv.Value >= threshold).Select(kv => kv.Key),
            StringComparer.Ordinal);

        if (universalLines.Count == 0)
            return normalizedTexts;

        // Hapus baris universal dari setiap halaman
        return normalizedTexts.Select(text =>
        {
            var filtered = SplitLines(text)
                .Where(line => !universalLines.Contains(line))
                .ToArray();
            return string.Join(' ', filtered);
        }).ToList();
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);

    // -----------------------------------------------------------------------
    // Jaccard similarity atas word-shingles
    // -----------------------------------------------------------------------

    /// <summary>
    /// Hasilkan set shingle (n-gram kata) dari teks ternormalisasi.
    /// Setiap shingle adalah n kata berurutan, digabung dengan separator null char
    /// agar tidak terjadi collision.
    /// </summary>
    private static HashSet<string> BuildShingles(string normalizedText, int n)
    {
        var words = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shingles = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i <= words.Length - n; i++)
        {
            shingles.Add(string.Join('\u0000', words, i, n));
        }

        return shingles;
    }

    /// <summary>
    /// Jaccard similarity = |A ∩ B| / |A ∪ B|.
    /// Mengembalikan 0.0 jika salah satu set kosong.
    /// </summary>
    private static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;

        var intersection = 0;
        foreach (var shingle in a)
        {
            if (b.Contains(shingle)) intersection++;
        }

        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string NormalizeWhitespace(string text) =>
        WhitespaceRegex.Replace(text, " ").Trim();

    private static string TruncateExcerpt(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
