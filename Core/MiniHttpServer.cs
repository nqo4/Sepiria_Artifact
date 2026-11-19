using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SephiriaLive.Core
{
    public class HttpRequestContext
    {
        public string Method;
        public string Path;
        public string Query;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body = "";

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

        /// <summary>쿼리스트링 값(디코딩 포함). 없으면 null.</summary>
        public string QueryParam(string key)
        {
            if (string.IsNullOrEmpty(Query)) return null;
            foreach (var part in Query.Split('&'))
            {
                var eq = part.IndexOf('=');
                var k = Uri.UnescapeDataString(eq >= 0 ? part.Substring(0, eq) : part);
                if (k == key) return eq >= 0 ? Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')) : "";
            }
            return null;
        }
    }

    public class HttpResponseData
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public string Body = "";

        public static HttpResponseData Json(object obj, int status = 200) =>
            new HttpResponseData { Status = status, Body = MiniJson.Write(obj) };

        public static HttpResponseData Html(string html) =>
            new HttpResponseData { ContentType = "text/html; charset=utf-8", Body = html ?? "" };

        public static HttpResponseData Text(string text, int status = 200) =>
            new HttpResponseData { Status = status, ContentType = "text/plain; charset=utf-8", Body = text ?? "" };
    }

    /// <summary>
    /// 127.0.0.1 전용 초경량 HTTP/1.1 서버 (TcpListener 기반 — HttpListener와 달리 URL ACL/관리자 권한 불필요,
    /// Unity Mono에서도 동작). 요청마다 연결을 닫는다(Connection: close).
    ///
    /// 보안: 외부 웹사이트가 브라우저를 통해 이 서버로 요청을 보내는 것(CSRF)을 막기 위해,
    /// Origin 헤더가 있는 요청은 허용 목록(로컬 페이지, file:// = "null")만 받는다.
    /// 핸들러는 백그라운드 스레드에서 호출된다 — Unity API가 필요하면 호출 측이 메인 스레드로 넘길 것.
    /// </summary>
    public class MiniHttpServer : IDisposable
    {
        public Action<string> Log = _ => { };
        public int Port { get; }
        public const int MaxBodyBytes = 1 << 20;

        readonly Dictionary<string, Func<HttpRequestContext, HttpResponseData>> _routes =
            new Dictionary<string, Func<HttpRequestContext, HttpResponseData>>(StringComparer.Ordinal);
        TcpListener _listener;
        Thread _acceptThread;
        volatile bool _running;

        public MiniHttpServer(int port) { Port = port; }

        public void Route(string method, string path, Func<HttpRequestContext, HttpResponseData> handler)
        {
            lock (_routes) _routes[method.ToUpperInvariant() + " " + path] = handler;
        }

        /// <summary>포트가 이미 사용 중이면 SocketException을 던진다(호출 측이 다음 후보 포트로 넘어가도록).</summary>
        public void Start()
        {
            if (_running) return;
            var listener = new TcpListener(IPAddress.Loopback, Port);
            try { listener.ExclusiveAddressUse = true; } catch { } // Windows 외 플랫폼에선 미지원일 수 있음
            listener.Start();
            _listener = listener;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "SephiriaLive-HTTP" };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        public void Dispose() => Stop();

        void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch
                {
                    if (!_running) return;
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true; // 브라우저가 아닌 클라이언트(curl, 테스트) 또는 동일 출처 GET
            if (origin == "null") return true;             // file:// 로 연 동반 페이지
            return origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}";
        }

        void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var stream = client.GetStream();
                    var ctx = ReadRequest(stream);
                    if (ctx == null) return;

                    var origin = ctx.Header("Origin");
                    HttpResponseData resp;
                    if (!IsAllowedOrigin(origin))
                    {
                        resp = HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "forbidden_origin", ["origin"] = origin }, 403);
                        WriteResponse(stream, resp, null);
                        return;
                    }

                    if (ctx.Method == "OPTIONS")
                    {
                        WriteResponse(stream, new HttpResponseData { Status = 204, ContentType = null }, origin);
                        return;
                    }

                    Func<HttpRequestContext, HttpResponseData> handler;
                    lock (_routes) _routes.TryGetValue(ctx.Method + " " + ctx.Path, out handler);
                    if (handler == null)
                    {
                        resp = HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "not_found", ["path"] = ctx.Path }, 404);
                    }
                    else
                    {
                        try { resp = handler(ctx) ?? HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "null_response" }, 500); }
                        catch (Exception ex)
                        {
                            Log($"[MiniHttpServer] 핸들러 예외 {ctx.Method} {ctx.Path}: {ex}");
                            resp = HttpResponseData.Json(new JObj { ["ok"] = false, ["error"] = "exception", ["message"] = ex.Message }, 500);
                        }
                    }
                    WriteResponse(stream, resp, origin);
                }
                catch (Exception ex)
                {
                    Log($"[MiniHttpServer] 연결 처리 실패: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        static HttpRequestContext ReadRequest(NetworkStream stream)
        {
            // 헤더는 \r\n\r\n 까지 바이트 단위로 읽는다 (본문은 Content-Length 만큼).
            var headerBytes = new List<byte>(1024);
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return null;
                headerBytes.Add((byte)b);
                if (headerBytes.Count > 64 * 1024) return null;
                int n = headerBytes.Count;
                if (n >= 4 && headerBytes[n - 4] == '\r' && headerBytes[n - 3] == '\n' && headerBytes[n - 2] == '\r' && headerBytes[n - 1] == '\n') break;
            }
            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var ctx = new HttpRequestContext { Method = first[0].ToUpperInvariant() };
            var target = first[1];
            var q = target.IndexOf('?');
            ctx.Path = q >= 0 ? target.Substring(0, q) : target;
            ctx.Query = q >= 0 ? target.Substring(q + 1) : "";

            for (int i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                ctx.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            if (int.TryParse(ctx.Header("Content-Length"), out var len) && len > 0)
            {
                if (len > MaxBodyBytes) throw new InvalidDataException("요청 본문이 너무 큽니다.");
                var buf = new byte[len];
                int read = 0;
                while (read < len)
                {
                    int n = stream.Read(buf, read, len - read);
                    if (n <= 0) break;
                    read += n;
                }
                ctx.Body = Encoding.UTF8.GetString(buf, 0, read);
            }
            return ctx;
        }

        static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }

        static void WriteResponse(NetworkStream stream, HttpResponseData resp, string allowedOrigin)
        {
            var body = Encoding.UTF8.GetBytes(resp.Body ?? "");
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(resp.Status).Append(' ').Append(ReasonPhrase(resp.Status)).Append("\r\n");
            if (!string.IsNullOrEmpty(resp.ContentType)) sb.Append("Content-Type: ").Append(resp.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (!string.IsNullOrEmpty(allowedOrigin))
            {
                sb.Append("Access-Control-Allow-Origin: ").Append(allowedOrigin).Append("\r\n");
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                sb.Append("Vary: Origin\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }
    }
}
