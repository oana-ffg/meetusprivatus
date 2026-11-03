using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Meetu;

public sealed record ModelAsset(string FileName, long Size, string Sha256, Uri Url);

// Fetches a public model when the user asks, in the helper process rather than the app. It uses the system proxy
// settings and needs no script host, both of which matter on managed machines. A dropped connection or closed
// app resumes from the partial file, and nothing replaces a real model file until its hash has been verified.
public sealed class ModelDownloader(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly ModelAsset SummaryModel = new(
        AppConfig.DefaultSummaryModelFile, 2_740_937_888, "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
        new Uri("https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/e87f176479d0855a907a41277aca2f8ee7a09523/Qwen3.5-4B-Q4_K_M.gguf"));

    private const int MaximumAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    public async Task DownloadAsync(ModelAsset asset, string destinationDirectory, Action<long, long> progress, CancellationToken cancellationToken)
    {
        LocalPath.Require(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var target = Path.Combine(destinationDirectory, asset.FileName);
        var partial = target + ".partial";
        // A link at either name could lead anywhere, including a network share.
        LocalPath.Require(target);
        LocalPath.Require(partial);
        if (File.Exists(target))
        {
            if (await IsVerifiedAsync(target, asset, cancellationToken)) { progress(asset.Size, asset.Size); return; }
            throw new InvalidDataException($"{asset.FileName} already exists but does not match. Move it aside and try again; it was not overwritten.");
        }
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadRemainderAsync(asset, partial, progress, cancellationToken);
                break;
            }
            catch (OversizedDownloadException)
            {
                File.Delete(partial);
                throw new InvalidDataException("The server sent more data than this model contains, so the download was discarded.");
            }
            catch (Exception exception) when (attempt < MaximumAttempts && IsTransient(exception) && !cancellationToken.IsCancellationRequested)
            {
                await delay(RetryDelay, cancellationToken);
            }
        }
        if (!await IsVerifiedAsync(partial, asset, cancellationToken))
        {
            File.Delete(partial);
            throw new InvalidDataException("The download did not match its expected checksum, so it was discarded. Try again.");
        }
        File.Move(partial, target);
    }

    private async Task DownloadRemainderAsync(ModelAsset asset, string partial, Action<long, long> progress, CancellationToken cancellationToken)
    {
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > asset.Size) { File.Delete(partial); offset = 0; }
        if (offset == asset.Size) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partial);
            throw new IOException("The server refused to resume the download.");
        }
        response.EnsureSuccessStatusCode();
        // A server may ignore the range request and send the whole file again.
        var resuming = response.StatusCode == HttpStatusCode.PartialContent;
        if (resuming && response.Content.Headers.ContentRange?.From != offset)
        {
            // Appending this would corrupt the file. Starting over is the only thing that can help.
            File.Delete(partial);
            throw new IOException("The server resumed the download from the wrong position.");
        }
        var received = resuming ? offset : 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(partial, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        var buffer = new byte[1 << 20];
        var lastReported = received;
        progress(received, asset.Size);
        while (await source.ReadAsync(buffer, cancellationToken) is var read and > 0)
        {
            // The pinned size bounds this, so a misbehaving server cannot fill the disk.
            if (received + read > asset.Size) throw new OversizedDownloadException();
            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            if (received - lastReported >= 16 << 20) { progress(received, asset.Size); lastReported = received; }
        }
        progress(received, asset.Size);
    }

    private static async Task<bool> IsVerifiedAsync(string path, ModelAsset asset, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != asset.Size) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken)) == asset.Sha256;
    }

    private sealed class OversizedDownloadException : Exception;

    // Client errors such as a missing file will not improve by trying again.
    private static bool IsTransient(Exception exception) => exception is IOException or TimeoutException ||
        exception is HttpRequestException { StatusCode: null or >= HttpStatusCode.InternalServerError };
}
