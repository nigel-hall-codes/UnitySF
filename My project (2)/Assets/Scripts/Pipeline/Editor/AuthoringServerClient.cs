using System;
using UnityEngine;
using UnityEngine.Networking;

namespace SFMap.Pipeline.Editor
{
    /// <summary>
    /// Thin, best-effort HTTP client for the authoring server (design #266), extracted from
    /// SFMapImporterWindow (#427).
    ///
    /// Every call is synchronous — the Editor import loop is synchronous, so we spin on
    /// <c>isDone</c> rather than pulling async/await into the flow; <c>timeout</c> bounds the
    /// spin so a stalled server can't hang the Editor UI thread. Every call is also non-fatal:
    /// a down / slow / erroring server logs a warning and returns <c>false</c>, never throwing,
    /// so a local bake with no authoring server still completes (most local bakes run that way).
    /// </summary>
    public static class AuthoringServerClient
    {
        /// <summary>
        /// Authoring server base URL. Public and settable so the host/port is configurable
        /// (a window field or environment override can point it elsewhere); defaults to the
        /// local dev server the server/ FastAPI app binds.
        /// </summary>
        public static string BaseUrl = "http://localhost:8000";

        const int TimeoutSeconds = 5;

        // Shorter than TimeoutSeconds: the probe runs once before a bulk import and its
        // whole point is to fail fast when the server is down, so a slow-but-listening host
        // can't stall the import's start for the full upload timeout.
        const int ProbeTimeoutSeconds = 2;

        /// <summary>
        /// One-shot reachability probe (#447): a single GET to the server root, used to decide
        /// up front whether a bulk import should do per-building renders + uploads at all.
        /// Returns true if the server answered <em>anything</em> — a ProtocolError (HTTP 4xx/5xx)
        /// still means a server is listening, which is all the caller needs to know; only a
        /// connection failure or timeout counts as unreachable. Quiet by design: it logs
        /// nothing, leaving the caller to emit the single "server down, skipping" warning.
        /// </summary>
        public static bool IsReachable()
        {
            string url = $"{BaseUrl}/";
            try
            {
                using var req = UnityWebRequest.Get(url);
                req.timeout = ProbeTimeoutSeconds;

                var op = req.SendWebRequest();
                while (!op.isDone) { }

                return req.result != UnityWebRequest.Result.ConnectionError;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Best-effort POST of a body to <c>BaseUrl + path</c>. True on 2xx.</summary>
        public static bool Post(string path, byte[] body, string contentType, string context)
            => Send("POST", path, body, contentType, context);

        /// <summary>Best-effort PUT of a body to <c>BaseUrl + path</c>. True on 2xx.</summary>
        public static bool Put(string path, byte[] body, string contentType, string context)
            => Send("PUT", path, body, contentType, context);

        static bool Send(string method, string path, byte[] body, string contentType, string context)
        {
            string url = $"{BaseUrl}{path}";
            try
            {
                using var req = new UnityWebRequest(url, method);
                req.uploadHandler   = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", contentType);
                req.timeout = TimeoutSeconds;

                var op = req.SendWebRequest();
                while (!op.isDone) { }

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[SFMapImporter] {context} to {url} " +
                                     $"failed ({req.responseCode}): {req.error}");
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                // Never abort the import for a server-side failure.
                Debug.LogWarning($"[SFMapImporter] {context} to {url} failed: {e.Message}");
                return false;
            }
        }
    }
}
