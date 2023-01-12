﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NLog.Targets.WebSocket
{
    internal class WebSocketServer
    {
        /// <summary>
        /// 服务端监听的端口  作为服务端口
        /// </summary>
        public int ListenPort { get; set; }

        /// <summary>
        /// WebSocket的路径
        /// </summary>
        public string WsPath { get; set; }

        // WebSocket监听的Uri
        public string Uri { get; private set; } = "";

        /// <summary>
        /// websocket 事件
        /// </summary>
        /// <param name="userToken">用户信息</param>
        /// <param name="data">数据</param>
        public delegate void WebSocketHandler(UserToken userToken, byte[]? data);

        /// <summary>
        /// 用户连接的事件
        /// </summary>
        public event WebSocketHandler? OnOpen;

        /// <summary>
        /// 用户断开的事件
        /// </summary>
        public event WebSocketHandler? OnClose;

        /// <summary>
        /// 收到用户消息的事件
        /// </summary>
        public event WebSocketHandler? OnMessage;

        /// <summary>
        /// 核心监听方法
        /// </summary>
        private HttpListener _listener;

        /// <summary>
        /// 异步发送队列
        /// </summary>
        private ConcurrentDictionary<UserToken, ConcurrentQueue<byte[]>> _sendQueue = new();

        /// <summary>
        /// 创建一个WebSocket服务
        /// </summary>
        /// <param name="port">监听端口</param>
        /// <param name="wsPath">路径</param>
        public WebSocketServer(int port, string wsPath)
        {
            this.ListenPort = port;
            WsPath = wsPath;
        }

        /// <summary>
        /// 获取当前配置的Uri
        /// </summary>
        /// <returns></returns>
        private string GetUri()
        {
            // 在win下先判断是不是管理员权限
            bool isWindowsAdmin = true;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                isWindowsAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            string path = this.WsPath;
            path = path.Trim('/');
            string uri;
            if (isWindowsAdmin)
                uri = $"http://*:{this.ListenPort}/{path}/";
            else
                uri = $"http://127.0.0.1:{this.ListenPort}/{path}/";

            return uri;
        }

        /// <summary>
        /// 开始监听
        /// </summary>
        /// <returns></returns>
        public WebSocketServer Listen()
        {
            this.Uri = GetUri();
            _listener = new HttpListener();
            _listener.Prefixes.Add(this.Uri);
            _listener.Start();

            Task.Run(async () =>
            {
                while (true)
                {
                    HttpListenerContext httpListenerContext = await this._listener.GetContextAsync();
                    if (!httpListenerContext.Request.IsWebSocketRequest)
                    {
                        return;
                    }
                    //来一个新的链接
                    ThreadPool.QueueUserWorkItem(r => { _ = Accept(httpListenerContext); });
                }
            });
            return this;
        }
        /// <summary>
        /// 一个新的连接
        /// </summary>
        /// <param name="s"></param>
        public async Task Accept(HttpListenerContext httpListenerContext)
        {
            HttpListenerWebSocketContext webSocketContext = await httpListenerContext.AcceptWebSocketAsync(null);
            UserToken userToken = new UserToken();
            userToken.ConnectTime = DateTime.Now;
            userToken.WebSocket = webSocketContext.WebSocket;
            userToken.RemoteAddress = httpListenerContext.Request.RemoteEndPoint;
            userToken.IPAddress = ((IPEndPoint)(userToken.RemoteAddress)).Address;
            userToken.IsWebSocket = true;
            try
            {
                #region 异步单队列发送任务
                _ = Task.Run(async () =>
                {
                    while (userToken.WebSocket != null && userToken.WebSocket.State == WebSocketState.Open)
                    {
                        if (_sendQueue.TryGetValue(userToken, out var queue))
                        {
                            while (!queue.IsEmpty)
                            {
                                if (queue.TryDequeue(out var data))
                                {
                                    await userToken.WebSocket.SendAsync(data, WebSocketMessageType.Text, true, CancellationToken.None);
                                }
                            }
                        }
                        else
                        {
                            await Task.Delay(100);
                        }
                    }
                });
                #endregion
                newAcceptHandler(userToken);
                var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024 * 1);
                try
                {
                    var WebSocket = userToken.WebSocket;
                    var bufferData = new List<byte>();
                    if (userToken.IsWebSocket)
                    {
                        while (WebSocket != null && WebSocket.State == WebSocketState.Open)
                        {
                            var result = await WebSocket.ReceiveAsync(buffer, CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await userToken.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            }
                            bufferData.AddRange(buffer.Take(result.Count).ToArray());
                            if (result.EndOfMessage)
                            {
                                var data = bufferData.ToArray();
                                _ = Task.Run(() =>
                                {
                                    OnMessage?.Invoke(userToken, data);
                                });
                                bufferData.Clear();
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception e)
            {
#if DEBUG
                Console.WriteLine(e.Message);
#endif
            }
            finally
            {
                newQuitHandler(userToken);
            }
        }
        private UserToken WebSocketHandshake(TcpClient s)
        {
            BinaryReader rs = new BinaryReader(s.GetStream());
            UserToken userToken = new UserToken();
            userToken.ConnectSocket = s.Client;
            userToken.ConnectTime = DateTime.Now;
            userToken.RemoteAddress = s.Client.RemoteEndPoint;
            userToken.IPAddress = ((IPEndPoint)(userToken.RemoteAddress)).Address;
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024 * 1);
            try
            {
                var length = rs.Read(buffer, 0, buffer.Length);
                string info = Encoding.UTF8.GetString(buffer.Take(length).ToArray());
                if (info.IndexOf("websocket") > -1)
                {
                    var send = userToken.ConnectSocket.Send(WebSocketHelper.HandshakeMessage(info));
                    if (send > 0)
                    {
                        userToken.IsWebSocket = true;
                        userToken.WebSocket = System.Net.WebSockets.WebSocket.CreateFromStream(s.GetStream(), true, null, TimeSpan.FromSeconds(5));
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return userToken;
        }
        /// <summary>
        /// 新的链接
        /// </summary>
        public void newAcceptHandler(UserToken userToken)
        {
            if (OnOpen != null)
            {
                OnOpen(userToken, null);
            }
        }

        /// <summary>
        /// 用户退出
        /// </summary>
        public void newQuitHandler(UserToken userToken)
        {
            if (OnClose != null)
            {
                OnClose(userToken, null);
            }
        }
        /// <summary>
        /// 对客户发送数据
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public void SendAsync(UserToken token, string data)
        {
            SendAsync(token, Encoding.UTF8.GetBytes(data));
        }
        /// <summary>
        /// 对客户发送数据
        /// </summary>
        /// <param name="socket"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public void SendAsync(UserToken token, byte[] data)
        {
            try
            {
                _sendQueue.AddOrUpdate(token, new ConcurrentQueue<byte[]>(new List<byte[]>() { data }), (k, v) =>
                {
                    v.Enqueue(data);
                    return v;
                });
            }
            catch (Exception)
            { }
        }
    }
}
