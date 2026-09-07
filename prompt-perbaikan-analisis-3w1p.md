# Prompt untuk Agentic AI (Antigravity — Claude Sonnet / Gemini Pro)
---

## PROMPT MULAI DI SINI

Kamu bekerja di repo **Document Assessment System 3W1P** (ASP.NET Core MVC, .NET 10). Aplikasi ini menerima upload PDF, mengirim seluruh isi PDF ke Google Gemini (`GeminiService.cs`) disertai *Master Prompt* panjang, dan menerima kembali JSON penilaian terstruktur (`AssessmentResult`) yang mencakup skor 3W1P + `documentAnalysis` (jumlah halaman, halaman kosong, karakter per halaman, anomali, redundansi).

Sebelum mengubah apa pun, **baca dulu** `GeminiService.cs`, `Models/AssessmentResult.cs`, dan `Controllers/HomeController.cs` secara utuh supaya paham struktur data dan alur eksisting. Jangan asumsikan struktur — verifikasi ke kode asli.

### Masalah yang harus diperbaiki

Saat ini, dua bagian dari `documentAnalysis` masih lemah karena 100% mengandalkan Gemini "menebak" dari tampilan visual PDF:

1. **`pageCharacterCounts`** — Gemini hanya mengestimasi jumlah karakter per halaman dari apa yang ia lihat secara visual. Ini tidak presisi/tidak deterministik — angka bisa berbeda-beda di setiap run untuk PDF yang sama.
2. **`redundantContent`** — deteksi redundansi cuma menangkap kasus yang isinya benar-benar identik 100% (misal slide 5 dan slide 6 sama persis). Kasus yang lebih halus — konten yang diparafrase, urutan kalimat diubah, atau kata-kata sedikit dimodifikasi tapi substansinya sama (indikasi "akal-akalan" supaya lolos deteksi) — tidak tertangkap.

### Kenapa ini terjadi

PDF dikirim ke Gemini sebagai `inline_data` (bytes mentah), lalu Gemini "membaca" secara visual/vision-based. Untuk hal yang sifatnya **bisa dihitung secara mekanis** (jumlah halaman, jumlah karakter per halaman, kemiripan teks antar halaman), menyerahkannya ke LLM vision itu tidak reliable — LLM cocok untuk *judgment* semantik (apakah checklist PASS/FAIL, apakah suatu redundansi "mencurigakan" secara konteks), tapi tidak cocok untuk *counting* yang butuh presisi 100%.

### Strategi perbaikan

Pindahkan bagian yang **bisa dihitung secara deterministik** ke kode C# (ekstraksi teks PDF asli), lalu:
- Suntikkan angka-angka hasil hitungan itu ke Master Prompt sebagai **ground truth** yang WAJIB dipakai Gemini (bukan ditebak ulang).
- Hitung juga **kandidat kemiripan antar halaman** secara algoritmik (bukan cuma exact match) dan sertakan ke prompt sebagai petunjuk, supaya Gemini tinggal melakukan *judgment* semantik: "apakah kandidat ini benar-benar redundansi yang mencurigakan sesuai definisi di Bagian 2B poin 5, atau kemiripan wajar?"
- Setelah respons JSON dari Gemini diterima, **timpa (override)** field `totalPagesUploaded`, `pageCharacterCounts`, `totalBlankPages`, `blankPageNumbers` dengan hasil hitungan C# yang otoritatif — supaya angka-angka ini selalu akurat 100% terlepas dari apa yang ditulis Gemini. Field `anomalies` dan `redundantContent` tetap hasil judgment Gemini (tapi sekarang dipandu data konkret, bukan menebak buta).

### Task implementasi (kerjakan berurutan)

**1. Tambahkan library ekstraksi PDF**
Tambahkan NuGet package `UglyToad.PdfPig` (pure .NET, MIT license, tidak butuh dependency native/OCR eksternal, cocok untuk ekstraksi teks per halaman + deteksi elemen gambar). Jangan pakai iText7 kalau bisa dihindari (lisensi AGPL bisa jadi masalah untuk proyek non-open-source).

**2. Buat service baru `PdfStructuralAnalysisService.cs`**
Buat class baru (di folder `Services/`) dengan method kurang lebih:

```csharp
public sealed class PdfStructuralAnalysisService
{
    public PdfStructuralAnalysis Analyze(byte[] pdfBytes)
    {
        // buka pdfBytes pakai PdfPig, iterasi tiap halaman
        // untuk tiap halaman hitung: teks mentah, characterCount, hasVisualContent
        // lalu hitung pairwise similarity antar semua halaman
    }
}
```

Definisikan model hasil (boleh sesuaikan nama, yang penting isinya):

```csharp
public sealed class PdfStructuralAnalysis
{
    public int TotalPages { get; init; }
    public List<PageStat> Pages { get; init; } = [];
    public List<RedundancyCandidate> RedundancyCandidates { get; init; } = [];
}

public sealed class PageStat
{
    public int Page { get; init; }
    public int CharacterCount { get; init; }
    public bool HasVisualContent { get; init; }
    public bool IsBlank { get; init; }
}

public sealed class RedundancyCandidate
{
    public int PageA { get; init; }
    public int PageB { get; init; }
    public double SimilarityScore { get; init; } // 0.0 - 1.0
    public string MatchedExcerpt { get; init; } = "";
}
```

**Aturan hitung per halaman:**
- `CharacterCount`: ambil teks halaman via PdfPig (`Page.Text`), normalisasi whitespace berlebih (collapse multiple spaces/newlines jadi satu), lalu `Trim().Length`. Ini angka **eksak**, bukan estimasi.
- `HasVisualContent`: `true` jika halaman punya `Page.GetImages()` yang tidak kosong, ATAU ada vector paths/graphics signifikan (cek `Page.ExperimentalAccess.Paths` atau setara di versi PdfPig yang dipakai — cek API yang tersedia di versi package yang ter-install).
- `IsBlank`: `true` hanya jika `CharacterCount == 0 && !HasVisualContent`.

**Aturan deteksi kandidat redundansi (jangan cuma exact match):**
1. Ambil teks tiap halaman, normalisasi (lowercase, hapus whitespace berlebih, hapus tanda baca ringan).
2. **Penting**: sebelum membandingkan, buang baris yang muncul identik di HAMPIR SEMUA halaman (header/footer/nomor halaman/watermark standar) — supaya tidak dihitung sebagai redundansi palsu.
3. Bandingkan tiap pasangan halaman (atau minimal halaman yang cukup panjang teksnya, skip halaman nyaris kosong) memakai metode kemiripan teks yang menangkap kemiripan **near-duplicate**, bukan cuma identik — misalnya Jaccard similarity atas word-shingles (n-gram kata, n=5 misalnya), atau normalized Levenshtein ratio. Pilih salah satu yang paling praktis diimplementasikan dengan library minim dependency (boleh implementasi manual, tidak perlu ML library berat).
4. Tandai sebagai kandidat (`RedundancyCandidate`) jika skor kemiripan di atas threshold (mulai dari ~0.65–0.75, boleh disesuaikan setelah testing) — supaya konten yang diparafrase/diubah sebagian tapi substansinya sama tetap tertangkap sebagai kandidat, tidak cuma yang 100% identik.
5. Simpan cuplikan singkat (`MatchedExcerpt`) dari salah satu halaman yang cocok, untuk konteks ke Gemini.

**3. Modifikasi `GeminiService.AnalyzeDocumentAsync`**
- Panggil `PdfStructuralAnalysisService.Analyze(pdfBytes)` sebelum membangun request ke Gemini.
- Tambahkan blok baru ke prompt (append ke `MasterPrompt` yang sudah ada, jangan hapus/ubah isi lain) berisi data hasil hitungan tadi, dengan instruksi tegas bahwa Gemini **wajib memakai angka ini apa adanya** untuk `totalPagesUploaded`, `pageCharacterCounts`, `isBlank` — bukan menghitung ulang/menebak sendiri. Contoh kerangka teks yang disisipkan (sesuaikan gaya bahasa dengan Master Prompt yang sudah ada di file, formal Bahasa Indonesia):

  > "DATA TEKNIS TERUKUR (WAJIB DIPAKAI APA ADANYA, JANGAN DIHITUNG/DITEBAK ULANG): total halaman = X. Untuk tiap halaman berikut adalah jumlah karakter teks yang benar-benar terekstrak dan status kontennya: [daftar page → characterCount, hasVisualContent]. Gunakan angka-angka ini persis untuk mengisi `pageCharacterCounts` dan menentukan `isBlank` (halaman blank hanya jika characterCount = 0 DAN hasVisualContent = false). Selain itu, sistem telah mendeteksi kandidat pasangan halaman dengan kemiripan teks tinggi secara algoritmik: [daftar pageA, pageB, skor kemiripan, cuplikan]. Untuk tiap kandidat ini, nilai secara semantik apakah ini benar-benar redundansi mencurigakan (copy-paste tanpa penyesuaian substansial) sesuai definisi Bagian 2B poin 5, atau kemiripan yang wajar (misal karena memang membahas hal yang sama secara legitimate) — lalu tetap cari juga redundansi lain yang mungkin tidak tertangkap kandidat algoritmik ini."

- Setelah `DeserializeAssessment(generatedJson)` berhasil dan sebelum `ValidateAssessment`, **timpa** `assessment.DocumentAnalysis.TotalPagesUploaded`, `PageCharacterCounts`, `TotalBlankPages`, dan `BlankPageNumbers` dengan hasil `PdfStructuralAnalysisService` (bukan hasil dari Gemini). Biarkan `Anomalies` dan `RedundantContent` tetap dari Gemini.
- Pastikan `ValidateDocumentAnalysis` tetap valid setelah override ini (harusnya otomatis valid karena datanya sekarang otoritatif dari kode, bukan dari AI).

**4. Cek `Models/AssessmentResult.cs`**
Pastikan properti `DocumentAnalysisResult` (`TotalPagesUploaded`, `PageCharacterCounts`, dll.) punya setter yang bisa ditulis ulang dari C# (kalau saat ini `init`-only dan langsung dari deserialisasi JSON, sesuaikan supaya bisa di-assign ulang setelah deserialisasi — atau bikin instance baru dengan nilai gabungan).

**5. Dependency Injection**
Daftarkan `PdfStructuralAnalysisService` di `Program.cs` (`builder.Services.AddSingleton<PdfStructuralAnalysisService>()` atau `AddScoped`, sesuaikan — service ini stateless jadi singleton aman) dan inject ke `GeminiService` lewat constructor.

**6. Jangan ubah:**
- Struktur JSON schema di `documentAnalysis` (field names harus tetap sama persis: `totalPagesUploaded`, `totalBlankPages`, `blankPageNumbers`, `pageCharacterCounts`, `anomalies`, `redundantContent`) — supaya `Result.cshtml` tidak perlu diubah.
- Isi Master Prompt yang sudah ada (Bagian 1–9) — hanya **tambahkan** blok data teknis baru, jangan hapus/reformat instruksi lain.
- Alur retry/backoff yang sudah ada di `AnalyzeDocumentAsync`.

### Kriteria penerimaan

- Upload PDF yang sama dua kali → `pageCharacterCounts` dan `totalPagesUploaded` menghasilkan angka **identik** di kedua run (deterministik, tidak lagi bervariasi antar run).
- Buat PDF uji dengan 2 halaman yang isinya sengaja diparafrase/diubah sebagian kata tapi substansi & strukturnya sama → sistem berhasil menandainya sebagai kandidat redundansi (tidak cuma menangkap yang identik 100%).
- Halaman yang cuma berisi gambar/diagram tanpa teks tetap `isBlank: false` (behavior lama tidak berubah).
- Build & jalankan aplikasi, upload 1 PDF contoh, pastikan `Result.cshtml` tetap tampil normal tanpa error.

Setelah selesai, ringkas perubahan apa saja yang kamu buat (file baru, file yang diubah, NuGet package yang ditambahkan) di akhir jawabanmu.

---

## PROMPT SELESAI
