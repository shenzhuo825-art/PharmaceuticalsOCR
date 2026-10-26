using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace PrescriptionScanner;

internal static class OcrWebServer
{
    private const long MaxImageBytes = 20 * 1024 * 1024;
    private static readonly SemaphoreSlim RecognitionGate = new(1, 1);

    public static void Run(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxImageBytes + 65536);
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = MaxImageBytes);
        var app = builder.Build();
        var webRoot = Path.GetFullPath(builder.Configuration["web-root"]
            ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web"));
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
            throw new DirectoryNotFoundException("找不到 web/index.html，請用 web/start.ps1 啟動。");
        // Serve public assets only, never uploads or source files.
        foreach (var (route, file, contentType) in new[] {
            ("/", "index.html", "text/html; charset=utf-8"),
            ("/index.html", "index.html", "text/html; charset=utf-8"),
            ("/styles.css", "styles.css", "text/css; charset=utf-8"),
            ("/app.js", "app.js", "text/javascript; charset=utf-8") })
        {
            app.MapGet(route, (HttpContext context) => {
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(Path.Combine(webRoot, file), contentType);
            });
        }
        var warmup = Task.Run(() => {
            try { Program.WarmUpOcr(); return "ready"; }
            catch (Exception error) {
                Console.Error.WriteLine($"OCR warmup failed: {error.GetType().Name}");
                return "unavailable";
            }
        });
        app.MapGet("/api/health", () => Results.Ok(new {
            status = "ok", model = warmup.IsCompletedSuccessfully ? warmup.Result : "loading"
        }));
        app.MapPost("/api/recognize", (Delegate)RecognizeAsync);
        app.Run();
    }

    private static async Task<IResult> RecognizeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}")
            return Error("請從本網站上傳圖片。", 403);
        if (!context.Request.HasFormContentType)
            return Error("請使用表單上傳圖片。", 400);
        if (!await RecognitionGate.WaitAsync(0))
            return Error("目前有另一張藥單正在辨識，請稍後再試。", 429);
        string? temporaryImage = null;
        try
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var image = form.Files.GetFile("image");
            if (image is null || image.Length == 0 || form.Files.Count != 1)
                return Error("請上傳一張藥單圖片。", 400);
            if (image.Length > MaxImageBytes)
                return Error("圖片不可超過 20 MB。", 413);
            if (!int.TryParse(form["tray"], out int tray) || tray < 1 || tray > 9999)
                return Error("藥盤編號須為 1～9999 的整數。", 400);
            string profile = form["mode"].ToString();
            if (profile.Length == 0) profile = "original";
            if (profile is not ("original" or "fast"))
                return Error("不支援的辨識模式。", 400);
            using var stream = image.OpenReadStream();
            var header = new byte[12];
            var count = await stream.ReadAtLeastAsync(header, 12, throwOnEndOfStream: false);
            string? extension = count >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff ? ".jpg"
                : count >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) ? ".png"
                : count == 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8) ? ".webp" : null;
            if (extension is null)
                return Error("圖片格式無效，請使用 JPG、PNG 或 WebP。", 400);
            temporaryImage = Path.Combine(Path.GetTempPath(), $"prescription-{Guid.NewGuid():N}{extension}");
            await using (var output = new FileStream(temporaryImage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(header.AsMemory(0, count), context.RequestAborted);
                await stream.CopyToAsync(output, context.RequestAborted);
            }
            var elapsed = Stopwatch.StartNew();
            // Keep the gate and file until OCR finishes, even on browser disconnect.
            string json = await Task.Run(() => Program.ParsePrescription(temporaryImage, tray, profile));
            context.Response.Headers["Server-Timing"] = $"ocr;dur={elapsed.Elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}";
            return Results.Content(json, "application/json; charset=utf-8");
        }
        catch (BadHttpRequestException error) { return Error("上傳失敗，圖片不可超過 20 MB。", error.StatusCode); }
        catch (InvalidDataException) { return Error("上傳資料無效或圖片超過 20 MB。", 400); }
        catch (OperationCanceledException) { return Error("上傳已中斷，請重新選取圖片。", 400); }
        catch (TimeoutException) { return Error("辨識逾時，請確認圖片清晰後重試。", 504); }
        catch (MultiplePrescriptionsException error) { return Error(error.Message, 422); }
        catch (Exception error)
        {
            Console.Error.WriteLine($"OCR request failed: {error.GetType().Name}");
            return Error("辨識失敗，請重試；若持續失敗，請確認電腦的 PaddleOCR 環境與圖片內容。", 500);
        }
        finally
        {
            if (temporaryImage is not null)
            {
                try { File.Delete(temporaryImage); }
                catch (IOException) { Console.Error.WriteLine("Could not remove temporary OCR image."); }
            }
            RecognitionGate.Release();
        }
    }
    private static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);
}
