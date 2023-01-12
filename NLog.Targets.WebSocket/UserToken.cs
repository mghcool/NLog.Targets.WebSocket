using System;
using System.Net;
using System.Net.Sockets;

namespace NLog.Targets.WebSocket
{
    /// <summary>
    /// 用户信息
    /// </summary>
    internal class UserToken
    {
        /// <summary>
        /// 连接的Socket对象
        /// </summary>
        public Socket ConnectSocket { get; set; }

        /// <summary>
        /// websocket对象
        /// </summary>
        public System.Net.WebSockets.WebSocket WebSocket { get; set; }

        /// <summary>
        /// 连接的时间
        /// </summary>
        public DateTime ConnectTime { get; set; }

        /// <summary>
        /// 远程地址
        /// </summary>
        public EndPoint RemoteAddress { get; set; }

        /// <summary>
        /// 客户端IP地址
        /// </summary>
        public IPAddress IPAddress { get; set; }

        /// <summary>
        /// 是否是 websocket（握手成功）
        /// </summary>
        public bool IsWebSocket { get; set; }
    }
}
