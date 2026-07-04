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
