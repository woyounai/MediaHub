using Microsoft.AspNetCore.StaticFiles;

static class MediaFormats
{
    static readonly FileExtensionContentTypeProvider Types = new();
    static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase);
    static MediaFormats()
    {
        Add("电影", "video/mp4", "mp4 m4v mp4v f4v");
        Add("电影", "video/x-matroska", "mkv mk3d");
        Add("电影", "video/webm", "webm");
        Add("电影", "video/x-msvideo", "avi divx");
        Add("电影", "video/quicktime", "mov qt");
        Add("电影", "video/x-ms-wmv", "wmv");
        Add("电影", "video/x-ms-asf", "asf");
        Add("电影", "video/mp2t", "ts m2ts mts m2t");
        Add("电影", "video/mpeg", "mpg mpeg mpe m1v m2v vob vro");
        Add("电影", "video/x-flv", "flv");
        Add("电影", "video/ogg", "ogv ogm");
        Add("电影", "video/3gpp", "3gp 3gpp");
        Add("电影", "video/3gpp2", "3g2");
        Add("电影", "application/vnd.rn-realmedia", "rm rmvb");
        Add("电影", "application/mxf", "mxf");
        Add("音乐", "audio/mpeg", "mp3 mp2 mpa");
        Add("音乐", "audio/mp4", "m4a m4b m4p");
        Add("音乐", "audio/aac", "aac adts");
        Add("音乐", "audio/flac", "flac");
        Add("音乐", "audio/wav", "wav wave");
        Add("音乐", "audio/ogg", "ogg oga opus");
        Add("音乐", "audio/x-ms-wma", "wma");
        Add("音乐", "audio/aiff", "aif aiff aifc");
        Add("音乐", "audio/x-ape", "ape");
        Add("音乐", "audio/wavpack", "wv");
        Add("音乐", "audio/ac3", "ac3 eac3 ec3");
        Add("音乐", "audio/vnd.dts", "dts dtshd");
        Add("音乐", "audio/dsf", "dsf");
        Add("音乐", "audio/dff", "dff");
        Add("音乐", "audio/amr", "amr");
        Add("音乐", "audio/midi", "mid midi");
        Add("照片", "image/jpeg", "jpg jpeg jpe jfif");
        Add("照片", "image/png", "png"); Add("照片", "image/gif", "gif");
        Add("照片", "image/webp", "webp"); Add("照片", "image/bmp", "bmp dib");
        Add("照片", "image/tiff", "tif tiff"); Add("照片", "image/avif", "avif");
        Add("照片", "image/heic", "heic"); Add("照片", "image/heif", "heif");
        Add("照片", "image/svg+xml", "svg"); Add("照片", "image/vnd.microsoft.icon", "ico");
        Add("照片", "image/x-raw", "raw dng cr2 cr3 nef arw orf raf rw2");
        // Containers/playlists are indexed but not advertised as directly playable streams.
        Add("电影", "application/octet-stream", "iso img bdmv");
        Add("文档", "application/octet-stream", "pdf doc docx xls xlsx ppt pptx txt md rtf csv tsv json xml yaml yml log epub mobi azw azw3 odt ods odp srt ass ssa sub vtt lrc cue m3u m3u8 pls");
    }
    static void Add(string category, string mime, string extensions)
    {
        foreach (var extension in extensions.Split(' ')) { Categories["." + extension] = category; Types.Mappings["." + extension] = mime; }
    }
    public static string CategoryFor(string path) => Directory.Exists(path) ? "文件夹" : Categories.GetValueOrDefault(Path.GetExtension(path), "其他");
    public static bool TryGetContentType(string path, out string mime) => Types.TryGetContentType(path, out mime!);
    public static bool IsPlayable(string path, bool includeImages = false)
    {
        var category = CategoryFor(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return category == "音乐" || category == "电影" && extension is not (".iso" or ".img" or ".bdmv") || includeImages && category == "照片";
    }
}
