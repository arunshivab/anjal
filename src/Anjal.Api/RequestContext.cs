using System.Net;
using System.Text;
using Anjal.Api.Dto;

namespace Anjal.Api;

/// <summary>
/// Wraps an <see cref="HttpListenerContext"/> with conveniences for
/// reading the body and writing JSON responses.
/// </summary>
public sealed class RequestContext
{
    private readonly HttpListenerContext context;
    private readonly int maxBodyBytes;
    private bool bodyRead;
    private string bodyCache = string.Empty;

    /// <summary>
    /// Construct with the underlying HttpListener context.
    /// </summary>
    /// <param name="context">The request being served.</param>
    /// <param name="maxBodyBytes">Max body size in bytes.</param>
    public RequestContext(HttpListenerContext context, int maxBodyBytes)
    {
        System.ArgumentNullException.ThrowIfNull(context);
        this.context = context;
        this.maxBodyBytes = maxBodyBytes;
    }

    /// <summary>HTTP method (GET, POST, etc.) in uppercase.</summary>
    public string Method => this.context.Request.HttpMethod.ToUpperInvariant();

    /// <summary>Absolute path of the URL (e.g. "/api/routing-rules").</summary>
    public string Path => this.context.Request.Url?.AbsolutePath ?? "/";

    /// <summary>The Authorization header value, or empty if not set.</summary>
    public string AuthorizationHeader => this.context.Request.Headers["Authorization"] ?? string.Empty;

    /// <summary>
    /// Read the request body as a string. Caches the result so repeated
    /// calls are free. Returns empty for GET / DELETE / requests with no body.
    /// </summary>
    /// <returns>UTF-8 body text.</returns>
    /// <exception cref="System.IO.InvalidDataException">If the body exceeds the configured max.</exception>
    public async System.Threading.Tasks.Task<string> ReadBodyAsync()
    {
        if (this.bodyRead)
        {
            return this.bodyCache;
        }

        long? declaredLength = this.context.Request.ContentLength64 < 0 ? null : this.context.Request.ContentLength64;
        if (declaredLength.HasValue && declaredLength.Value > this.maxBodyBytes)
        {
            throw new System.IO.InvalidDataException($"Body exceeds maximum of {this.maxBodyBytes} bytes.");
        }

        using var ms = new System.IO.MemoryStream();
        byte[] buf = new byte[8192];
        int totalRead = 0;
        while (true)
        {
            int n = await this.context.Request.InputStream.ReadAsync(buf).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }
            totalRead += n;
            if (totalRead > this.maxBodyBytes)
            {
                throw new System.IO.InvalidDataException($"Body exceeds maximum of {this.maxBodyBytes} bytes.");
            }
            ms.Write(buf, 0, n);
        }

        this.bodyCache = Encoding.UTF8.GetString(ms.ToArray());
        this.bodyRead = true;
        return this.bodyCache;
    }

    /// <summary>
    /// Write a JSON body and an HTTP status code, then close the response.
    /// </summary>
    /// <typeparam name="T">The response body type.</typeparam>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="body">The response body to serialise.</param>
    public async System.Threading.Tasks.Task WriteJsonAsync<T>(int statusCode, T body)
    {
        string json = ApiJson.Serialize(body);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        this.context.Response.StatusCode = statusCode;
        this.context.Response.ContentType = "application/json; charset=utf-8";
        this.context.Response.ContentLength64 = bytes.Length;
        await this.context.Response.OutputStream.WriteAsync(bytes.AsMemory(0, bytes.Length)).ConfigureAwait(false);
        this.context.Response.OutputStream.Close();
    }

    /// <summary>
    /// Write a standard error response with status code and JSON body.
    /// </summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="error">Machine-readable code (e.g. "invalid_request").</param>
    /// <param name="message">Human-readable message.</param>
    public System.Threading.Tasks.Task WriteErrorAsync(int statusCode, string error, string message)
    {
        return this.WriteJsonAsync(statusCode, new ErrorResponse
        {
            Error = error,
            Message = message,
        });
    }

    /// <summary>
    /// Write an empty response with the given status code (used for 204 etc.).
    /// </summary>
    /// <param name="statusCode">HTTP status code.</param>
    public System.Threading.Tasks.Task WriteEmptyAsync(int statusCode)
    {
        this.context.Response.StatusCode = statusCode;
        this.context.Response.ContentLength64 = 0;
        this.context.Response.OutputStream.Close();
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
