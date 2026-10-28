using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using static EmbeddingSearchViewer_App.Services.DiagHttp;

namespace EmbeddingSearchViewer_App.Services;

/// <summary>The hand-written HTTP/1.1 plumbing of the diagnostics API: request parsing and response writing.</summary>
internal static class DiagHttp
{
    internal const int MaxHeaderBytes = 16 * 1024;
    internal const int MaxBodyBytes = 64 * 1024;

    /// <summary>Reads one HTTP/1.1 request (headers and, with Content-Length, the body); null when it cannot be used.</summary>
    internal static async Task<DiagRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var head = new MemoryStream();
        int headerEnd;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) return null;
            head.Write(buffer, 0, read);
            headerEnd = IndexOfHeaderEnd(head.GetBuffer(), (int)head.Length);
            if (headerEnd >= 0) break;
            if (head.Length > MaxHeaderBytes) return null;
        }

        string headerText = Encoding.Latin1.GetString(head.GetBuffer(), 0, headerEnd);
        string[] lines = headerText.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        // An absolute-form target (http://127.0.0.1:port/v1/...) is accepted as well as the usual origin-form.
        string target = requestLine[1];
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var absolute)) return null;
            target = absolute.PathAndQuery;
        }

        string path = target;
        string query = string.Empty;
        int question = target.IndexOf('?');
        if (question >= 0)
        {
            path = target[..question];
            query = target[(question + 1)..];
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        if (headers.ContainsKey("Transfer-Encoding")) return null; // only Content-Length bodies are served

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? lengthText)
            && (!int.TryParse(lengthText, out contentLength) || contentLength < 0))
        {
            return null;
        }
        if (contentLength > MaxBodyBytes) return null;

        var body = new byte[contentLength];
        int headerLength = headerEnd + 4;
        int buffered = Math.Min((int)head.Length - headerLength, contentLength);
        if (buffered > 0) Array.Copy(head.GetBuffer(), headerLength, body, 0, buffered);

        int offset = buffered;
        while (offset < contentLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }

        return new DiagRequest(requestLine[0].ToUpperInvariant(), path, ParseQuery(query), headers, body);
    }

    internal static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        }
        return -1;
    }

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query.Length == 0) return values;

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals < 0) continue;

            string key = Unescape(pair[..equals]);
            if (key.Length > 0) values[key] = Unescape(pair[(equals + 1)..]);
        }
        return values;
    }

    internal static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    internal static string ReasonOf(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        500 => "Internal Server Error",
        _ => "Unknown",
    };

    internal static async Task WriteAsync(NetworkStream stream, int status, string reason, string json, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        string head = $"HTTP/1.1 {status} {reason}\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + (status == 401 ? "WWW-Authenticate: Bearer\r\n" : string.Empty)
            + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head).AsMemory(), token).ConfigureAwait(false);
        await stream.WriteAsync(body.AsMemory(), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}

internal sealed record DiagRequest(
    string Method,
    string Path,
    Dictionary<string, string> Query,
    Dictionary<string, string> Headers,
    byte[] Body);
