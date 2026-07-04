using System.Text;
using NUnit.Framework;
using SFMap.Pipeline.Editor;

namespace SFMap.Tests
{
    /// <summary>
    /// Verifies the intentional behaviour of #427: authoring-server uploads are best-effort,
    /// so an import against a *down* server degrades gracefully (returns false, never throws)
    /// instead of aborting the bake. Points BaseUrl at a port nothing is listening on.
    /// </summary>
    public class AuthoringServerClientTests
    {
        string _saved;

        [SetUp]
        public void PointAtDeadServer()
        {
            _saved = AuthoringServerClient.BaseUrl;
            // A port nothing is bound to → connection refused → best-effort failure path.
            AuthoringServerClient.BaseUrl = "http://127.0.0.1:59321";
        }

        [TearDown]
        public void Restore()
        {
            AuthoringServerClient.BaseUrl = _saved;
        }

        [Test]
        public void BaseUrlIsConfigurable()
        {
            AuthoringServerClient.BaseUrl = "http://example.test:1234";
            Assert.AreEqual("http://example.test:1234", AuthoringServerClient.BaseUrl);
        }

        [Test]
        public void PostToDownServerReturnsFalseAndDoesNotThrow()
        {
            byte[] body = Encoding.UTF8.GetBytes("{}");
            bool ok = false;
            Assert.DoesNotThrow(() =>
                ok = AuthoringServerClient.Post("/buildings/import-sidecar", body,
                                                "application/json", "test: sidecar"));
            Assert.IsFalse(ok, "a POST to a down server must report failure, not success");
        }

        [Test]
        public void PutToDownServerReturnsFalseAndDoesNotThrow()
        {
            byte[] body = new byte[] { 1, 2, 3 };
            bool ok = false;
            Assert.DoesNotThrow(() =>
                ok = AuthoringServerClient.Put("/buildings/42/thumb", body,
                                               "image/jpeg", "test: thumb"));
            Assert.IsFalse(ok, "a PUT to a down server must report failure, not success");
        }
    }
}
