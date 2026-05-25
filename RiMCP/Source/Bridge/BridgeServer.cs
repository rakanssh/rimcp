using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using RiMCP.Util;

namespace RiMCP.Bridge
{
    internal sealed class BridgeServer
    {
        private const int MaxRequestBodyBytes = 16 * 1024;

        private readonly Func<BridgeRequest, BridgeResponse> dispatch;
        private readonly RecentLog log;
        private readonly HashSet<string> recentClients = new HashSet<string>();
        private HttpListener listener;
        private Thread thread;
        private volatile bool stopping;
        private string token;

        public BridgeServer(int port, string token, Func<BridgeRequest, BridgeResponse> dispatch, RecentLog log)
        {
            Port = port;
            this.token = token;
            this.dispatch = dispatch;
            this.log = log;
        }

        public int Port { get; private set; }
        public bool IsRunning { get; private set; }
        public DateTime? LastRequestUtc { get; private set; }

        public int RecentClientCount
        {
            get
            {
                lock (recentClients)
                {
                    return recentClients.Count;
                }
            }
        }

        public void UpdateToken(string newToken)
        {
            token = newToken;
        }

        public void Start()
        {
            stopping = false;
            listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            listener.Start();
            IsRunning = true;
            log.Add("Bridge started on 127.0.0.1:" + Port);

            thread = new Thread(ListenLoop);
            thread.IsBackground = true;
            thread.Name = "RiMCP";
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            IsRunning = false;
            try
            {
                if (listener != null)
                {
                    listener.Close();
                }
            }
            catch
            {
            }
            log.Add("Bridge stopped");
        }

        private void ListenLoop()
        {
            while (!stopping)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleContext(context));
                }
                catch
                {
                    if (!stopping)
                    {
                        log.Add("Bridge listener error");
                    }
                }
            }
        }

        private void HandleContext(HttpListenerContext context)
        {
            LastRequestUtc = DateTime.UtcNow;
            string client = context.Request.RemoteEndPoint == null ? "unknown" : context.Request.RemoteEndPoint.Address.ToString();
            lock (recentClients)
            {
                recentClients.Add(client);
            }

            BridgeResponse response;
            try
            {
                if (!IsOriginAllowed(context.Request.Headers["Origin"]))
                {
                    response = BridgeResponse.Error(403, "Origin is not allowed.");
                }
                else if (!IsAuthorized(context.Request))
                {
                    response = BridgeResponse.Error(401, "Missing or invalid bearer token.");
                }
                else if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/health")
                {
                    response = BridgeResponse.Json(200, Json.Object(
                        Json.Prop("status", Json.String("ok")),
                        Json.Prop("readOnly", Json.Bool(false)),
                        Json.Prop("commands", Json.Bool(true)),
                        Json.Prop("port", Json.Number(Port))));
                }
                else
                {
                    response = dispatch(new BridgeRequest(context.Request.HttpMethod, context.Request.Url, ReadBody(context.Request)));
                }
            }
            catch (RequestBodyTooLargeException ex)
            {
                response = BridgeResponse.Error(413, ex.Message);
            }
            catch (Exception ex)
            {
                response = BridgeResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
            }

            WriteResponse(context, response);
            log.Add(context.Request.HttpMethod + " " + context.Request.Url.PathAndQuery + " -> " + response.StatusCode);
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            if (request.HttpMethod == "GET" || request.InputStream == null)
            {
                return null;
            }
            if (request.ContentLength64 > MaxRequestBodyBytes)
            {
                throw new RequestBodyTooLargeException("Request body is too large.");
            }

            Encoding encoding = request.ContentEncoding ?? Encoding.UTF8;
            using (MemoryStream body = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int total = 0;
                int read;
                while ((read = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxRequestBodyBytes)
                    {
                        throw new RequestBodyTooLargeException("Request body is too large.");
                    }
                    body.Write(buffer, 0, read);
                }
                return encoding.GetString(body.ToArray());
            }
        }

        private sealed class RequestBodyTooLargeException : Exception
        {
            public RequestBodyTooLargeException(string message) : base(message)
            {
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            string header = request.Headers["Authorization"];
            return !string.IsNullOrEmpty(token) && header == "Bearer " + token;
        }

        private static bool IsOriginAllowed(string origin)
        {
            if (string.IsNullOrEmpty(origin))
            {
                return true;
            }

            Uri uri;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out uri))
            {
                return false;
            }

            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteResponse(HttpListenerContext context, BridgeResponse response)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(response.Body ?? string.Empty);
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = response.ContentType + "; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            using (Stream output = context.Response.OutputStream)
            {
                output.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
