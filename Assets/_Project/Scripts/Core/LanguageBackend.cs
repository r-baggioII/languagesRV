using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// Only the notebook URL is persisted. PINs and bearer credentials live in memory.
public static class LanguageBackend
{
    public const string TokenHeader = "X-Language-Token";
    public const string HealthPath = "/api/health";
    public const string PairPath = "/api/pair";
    private const string UrlPreference = "LanguageImmersion.BackendUrl";

    private static string baseUrl = PlayerPrefs.GetString(UrlPreference, string.Empty);
    private static string sessionToken;
    private static DateTime tokenExpiresUtc;
    private static int sessionRevision;
    private static string sessionIssue;

    public static string BaseUrl => string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl;
    public static string Token
    {
        get
        {
            if (sessionToken != null && DateTime.UtcNow >= tokenExpiresUtc)
                ClearSessionToken("Session expired. Read a new PIN from the notebook and pair again.");
            return sessionToken;
        }
    }
    public static bool IsConfigured => BaseUrl != null && Token != null;
    public static int SessionRevision => sessionRevision;
    public static string SessionIssue => sessionIssue;

    public static bool TrySetBaseUrl(string address, out string error)
    {
        if (!TryNormalizeUrl(address, out string normalized, out error))
            return false;
        if (!string.Equals(baseUrl, normalized, StringComparison.OrdinalIgnoreCase))
            ClearSessionToken("Backend address changed. Enter a new PIN to pair.");
        baseUrl = normalized;
        PlayerPrefs.SetString(UrlPreference, normalized);
        PlayerPrefs.Save();
        return true;
    }

    public static void ClearSessionToken(string reason = "Pair with the notebook to continue.")
    {
        sessionToken = null;
        tokenExpiresUtc = DateTime.MinValue;
        sessionIssue = reason;
        sessionRevision++;
    }

    // A late 401 from an old request must not invalidate a newer pairing.
    public static void RejectToken(string rejectedToken)
    {
        if (!string.IsNullOrEmpty(rejectedToken) &&
            string.Equals(sessionToken, rejectedToken, StringComparison.Ordinal))
            ClearSessionToken("Session rejected or expired. Read a new PIN and pair again.");
    }

    public static async Task<PairResult> PairAsync(string pin, CancellationToken cancellationToken = default)
    {
        if (pin == null || pin.Length != 6)
            return new PairResult(false, "Enter exactly six digits from the notebook.");
        for (int i = 0; i < pin.Length; i++)
            if (pin[i] < '0' || pin[i] > '9')
                return new PairResult(false, "PIN must contain six digits only.");
        if (BaseUrl == null)
            return new PairResult(false, "Enter the notebook backend address first.");

        string pairedUrl = BaseUrl;
        int startingRevision = sessionRevision;
        // No authentication header on this request: the one-time PIN is its body.
        using (var request = new UnityWebRequest(BuildUrl(pairedUrl, PairPath), UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(
                JsonUtility.ToJson(new PairRequest { pin = pin })));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 10;
            try
            {
                await SendAsync(request, cancellationToken);
                if (request.responseCode == 401)
                    return new PairResult(false, "Wrong PIN. Check the notebook terminal and try again.");
                if (request.responseCode == 410)
                    return new PairResult(false, "PIN expired. Read the new PIN in the notebook terminal.");
                if (request.responseCode == 429)
                    return new PairResult(false, "Too many attempts. Wait and use a new notebook PIN.");
                if (request.responseCode == 422)
                    return new PairResult(false, "Invalid PIN format. Enter exactly six digits.");
                if (request.responseCode == 404)
                    return new PairResult(false, "Backend does not provide POST /api/pair.");
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                    return new PairResult(false, "Pairing failed. Check notebook, Wi-Fi and backend address (HTTP " + request.responseCode + ").");

                PairResponse response;
                try { response = JsonUtility.FromJson<PairResponse>(request.downloadHandler.text); }
                catch (ArgumentException) { response = null; }
                if (response == null || string.IsNullOrWhiteSpace(response.token) || response.expires_in <= 0)
                    return new PairResult(false, "Backend returned an invalid pairing response.");

                // Validate the candidate credential before making it available to services.
                HealthResult health = await CheckHealthAsync(pairedUrl, response.token, cancellationToken);
                if (pairedUrl != BaseUrl || startingRevision != sessionRevision)
                    return new PairResult(false, "Backend settings changed. Pair again.");
                if (!health.Success)
                    return new PairResult(false, "PIN accepted, but " + health.Message + " Read a new PIN and pair again.");

                sessionToken = response.token;
                tokenExpiresUtc = DateTime.UtcNow.AddSeconds(response.expires_in);
                sessionIssue = null;
                sessionRevision++;
                return new PairResult(true, "Connected and authenticated. AI services need a live conversation test.");
            }
            catch (OperationCanceledException)
            {
                return new PairResult(false, "Pairing canceled.");
            }
            catch (Exception)
            {
                return new PairResult(false, "Could not reach the notebook backend. Check Wi-Fi and address.");
            }
        }
    }

    public static bool TryNormalizeUrl(string address, out string normalized, out string error)
    {
        normalized = null;
        error = null;
        string candidate = address?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            error = "Enter the notebook's LAN IP address.";
            return false;
        }
        if (!candidate.Contains("://")) candidate = "http://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath != "/")
        {
            error = "Use the notebook's LAN IP address and port 8081.";
            return false;
        }
        var builder = new UriBuilder(uri) { Path = string.Empty, Query = string.Empty, Fragment = string.Empty };
        if (uri.IsDefaultPort) builder.Port = 8081;
        normalized = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    public static string MissingConfigurationMessage() => BaseUrl == null
        ? "Set the notebook backend address in VR Settings."
        : "Pair with the notebook's six-digit PIN in VR Settings.";

    public static string BuildUrl(string address, string path) => address + path;

    public static string LiveWebSocketUrl()
    {
        if (BaseUrl == null) return null;
        Uri uri = new Uri(BaseUrl);
        var builder = new UriBuilder(uri) {
            Scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "/api/gemini/live", Query = string.Empty, Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    public static string KokoroDirectUrl()
    {
        if (BaseUrl == null) return null;
        var builder = new UriBuilder(new Uri(BaseUrl)) {
            Port = 8000, Path = "/tts", Query = string.Empty, Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    public static void ApplyAuth(UnityWebRequest request, string token) =>
        request.SetRequestHeader(TokenHeader, token);

    public static async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        string token = Token;
        if (BaseUrl == null || token == null) return MissingConfigurationMessage();
        HealthResult result = await CheckHealthAsync(BaseUrl, token, cancellationToken);
        if (result.AuthRejected) RejectToken(token);
        return result.Message;
    }

    private static async Task<HealthResult> CheckHealthAsync(string url, string token, CancellationToken cancellationToken)
    {
        using (var request = UnityWebRequest.Get(BuildUrl(url, HealthPath)))
        {
            request.timeout = 6;
            ApplyAuth(request, token);
            await SendAsync(request, cancellationToken);
            if (request.responseCode == 401 || request.responseCode == 403)
                return new HealthResult(false, true, "authentication was rejected.");
            if (request.responseCode == 404)
                return new HealthResult(false, false, "GET /api/health is unavailable.");
            if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                return new HealthResult(false, false, "health check failed (HTTP " + request.responseCode + ").");
            return new HealthResult(true, false, "Connected and authenticated. AI services need a live conversation test.");
        }
    }

    private static async Task SendAsync(UnityWebRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = request.SendWebRequest();
        while (!operation.isDone)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                request.Abort();
                cancellationToken.ThrowIfCancellationRequested();
            }
            await Task.Yield();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static string Redact(string text, string token) =>
        string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)
            ? text : text.Replace(token, "[redacted]");

    public readonly struct PairResult
    {
        public readonly bool Success;
        public readonly string Message;
        public PairResult(bool success, string message) { Success = success; Message = message; }
    }

    private readonly struct HealthResult
    {
        public readonly bool Success;
        public readonly bool AuthRejected;
        public readonly string Message;
        public HealthResult(bool success, bool authRejected, string message)
        { Success = success; AuthRejected = authRejected; Message = message; }
    }

    [Serializable] private sealed class PairRequest { public string pin; }
    [Serializable] private sealed class PairResponse { public string token; public int expires_in; }
}
