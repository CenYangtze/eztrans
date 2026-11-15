namespace SnapTrans.Core.Ocr;

public sealed record OcrModelFile(string Role, string FileName, Uri DownloadUri, string Sha256, long SizeBytes);
public sealed record OcrModelPreset(string Id, string Name, string ModelFamily, IReadOnlyList<string> Languages, IReadOnlyList<OcrModelFile> Files)
{
    public long DownloadBytes => Files.Sum(file => file.SizeBytes);
    public bool SupportsLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag == "auto") return true;
        string language = tag.Split('-')[0];
        return Languages.Contains(language, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Immutable, reviewed model/dictionary pairs. Never trust a hash downloaded alongside an artifact.</summary>
public static class OcrModelCatalog
{
    public const string PublisherRevision = "708cae2fcb88720e1d891a81b5ee3e8b2bcc139e";
    public const string Source = "https://github.com/BobLd/RapidOcrNet/tree/" + PublisherRevision + "/RapidOcrNet/models";
    private const string Base = "https://raw.githubusercontent.com/BobLd/RapidOcrNet/" + PublisherRevision + "/RapidOcrNet/models/";
    private static OcrModelFile File(string role, string path, string sha256, long size) => new(role, path.Split('/')[^1], new Uri(Base + path), sha256, size);
    private static readonly OcrModelFile Classifier = File("classifier", "v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx", "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7", 1018508);

    public static IReadOnlyList<OcrModelPreset> Presets { get; } =
    [
        new("latin-v5", "PP-OCRv5 Latin · 约 14 MB", "v5",
            ["en", "fr", "de", "es", "it", "pt", "nl", "pl", "tr", "vi", "id", "sv", "da", "no", "fi", "cs", "ro", "hu"],
            [File("detector", "v5/ch_PP-OCRv5_mobile_det.onnx", "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae", 4819576), Classifier,
             File("recognizer", "v5/latin_PP-OCRv5_rec_mobile_infer.onnx", "b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a", 7904513),
             File("dictionary", "v5/ppocrv5_latin_dict.txt", "3c0a8a79b612653c25f765271714f71281e4e955962c153e272b7b8c1d2b13ff", 1634)]),
        new("multilingual-v6-small", "PP-OCRv6 Small · 中英日/多语言 · 约 32 MB", "v6",
            ["zh", "ja", "en", "fr", "de", "es", "it", "pt", "nl", "pl", "tr", "vi", "id", "sv", "da", "no", "fi", "cs", "ro", "hu"],
            [File("detector", "v6/PP-OCRv6_det_small.onnx", "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f", 9929594), Classifier,
             File("recognizer", "v6/PP-OCRv6_rec_small.onnx", "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884", 21234383),
             File("dictionary", "v6/ppocrv6_dict.txt", "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d", 74947)])
    ];

    public static OcrModelPreset Get(string id) => Presets.FirstOrDefault(preset => preset.Id == id)
        ?? throw new ArgumentException("未知 OCR 模型预设，请从受支持的配套模型列表选择。", nameof(id));
}

public enum OcrModelState { Missing, Ready, Invalid }
public sealed record OcrModelStatus(OcrModelState State, string Message, IReadOnlyList<string> MissingFiles)
{
    public bool IsReady => State == OcrModelState.Ready;
}
public sealed record OcrModelDownloadProgress(string FileName, long DownloadedBytes, long TotalBytes)
{
    public double Percentage => TotalBytes > 0 ? Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100) : 0;
}
public sealed record OcrModelPaths(string Detector, string Classifier, string Recognizer, string Dictionary);

/// <summary>A separately deployed OCR assembly implements this contract; Core never references ONNX Runtime.</summary>
public interface IOcrEnginePlugin
{
    string Id { get; }
    string Name { get; }
    IOcrEngine Create(string modelFolder, string modelPreset);
}
