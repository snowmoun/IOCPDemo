﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Threading;
using System.Net;

namespace IOCPDemo
{

    class Server
    {

        // 监听Socket，用于接受客户端的连接请求
        private Socket listenSocket;

        // 用于服务器执行的互斥同步对象
        private static Mutex mutex = new Mutex();

        // 用于每个I/O Socket操作的缓冲区大小
        private Int32 bufferSize;

        // 服务器上连接的客户端总数
        private Int32 numConnectedSockets;

        // 服务器能接受的最大连接数量
        private Int32 numConnections;

        // 完成端口上进行投递所用的连接对象池
        private SocketAsyncEventArgsPool eventArgsPool;

        // 消息的串行化
        private MessageSerializer serializer;

        // 构造函数，建立一个未初始化的服务器实例
        public Server(Int32 numConnections = 1024, Int32 bufferSize = 8192)
        {
            this.numConnectedSockets = 0;
            this.numConnections = numConnections;
            this.bufferSize = bufferSize;

            eventArgsPool = new SocketAsyncEventArgsPool(numConnections);
            serializer = new MessageSerializer();

            // 为IoContextPool预分配SocketAsyncEventArgs对象
            for (Int32 i = 0; i < this.numConnections; i++)
            {
                SocketAsyncEventArgs eventArg = new SocketAsyncEventArgs();
                MessageUserToken msgUserToken = new MessageUserToken();
                msgUserToken.MessageReceived += new EventHandler<MessageEventArgs>(OnMessageReceived);
                eventArg.UserToken = msgUserToken;

                eventArg.Completed += new EventHandler<SocketAsyncEventArgs>(OnIOCompleted);
                eventArg.SetBuffer(new Byte[this.bufferSize], 0, this.bufferSize);
                //Console.WriteLine("Server:initEventArg: {0}", eventArg.GetHashCode());
                // 将预分配的对象加入SocketAsyncEventArgs对象池中
                eventArgsPool.Push(eventArg);
            }
        }

        // 启动服务，开始监听
        public void Start(Int32 port)
        {
            Console.WriteLine("[Server] Server start: {0}", port);
            // 获得主机相关信息
            IPAddress[] addressList = Dns.GetHostEntry(Environment.MachineName).AddressList;
            Console.WriteLine("[Server] IP Addresses: {0}. Machine name: {1}", addressList, Environment.MachineName);
            IPEndPoint localEndPoint = new IPEndPoint(addressList[addressList.Length - 1], port);

            // 创建监听socket
            listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.ReceiveBufferSize = bufferSize;
            listenSocket.SendBufferSize = bufferSize;

            if (localEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                //Console.WriteLine("Server start. ipv6: {0}", localEndPoint);

                // 配置监听socket为 dual-mode (IPv4 & IPv6) 
                // 27 is equivalent to IPV6_V6ONLY socket option in the winsock snippet below,
                listenSocket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)27, false);
                listenSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, localEndPoint.Port));
            }
            else
            {
                //Console.WriteLine("Server start. ipv4: {0}", localEndPoint);
                listenSocket.Bind(localEndPoint);
            }

            // 开始监听
            this.listenSocket.Listen(this.numConnections);

            // 在监听Socket上投递一个接受请求。
            this.StartAccept(null);

            // Blocks the current thread to receive incoming messages.
            mutex.WaitOne();
        }

        // 停止服务
        public void Stop()
        {
            listenSocket.Close();
            mutex.ReleaseMutex();
        }

        // 当Socket上的发送或接收请求被完成时，调用此函数
        private void OnIOCompleted(object sender, SocketAsyncEventArgs e)
        {
            //Console.WriteLine("ONIOCompleted");
            // Determine which type of operation just completed and call the associated handler.
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    this.ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    this.ProcessSend(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }
        }

        private void OnMessageReceived(object sender, MessageEventArgs e)
        {
            HelloMessage msg = (HelloMessage) e.Message;
            ProcessHelloMessage((SocketAsyncEventArgs)sender, msg);
        }

        private void ProcessHelloMessage(SocketAsyncEventArgs e, HelloMessage msg)
        {
            Console.WriteLine("[Server] ProcessHelloMessage: received client msg: '{0}'.", msg.Message);
            Console.WriteLine("[Server] ProcessHelloMessage: send welcome message back to client.");
            HelloMessage welcomeMsg = new HelloMessage
            {
                Type = (Int32)MessageType.Hello,
                Direction = (Int32)MessageDirection.FromServer,
                ID = msg.ID,
                SessionID = msg.SessionID,
                Message = "Welcome to server. client #" + msg.SessionID.ToString(),
            };
            Byte[] buffer = this.serializer.SerializeWithPrefix(welcomeMsg);
            Socket socket = e.AcceptSocket;
            Buffer.BlockCopy(buffer, 0, e.Buffer, 0, buffer.Length);
            //投递发送请求，这个函数有可能同步发送出去，这时返回false，并且不会引发SocketAsyncEventArgs.Completed事件
            e.SetBuffer(0, buffer.Length);
            if (!socket.SendAsync(e))
            {
                // 同步发送时处理发送完成事件
                ProcessSend(e);
            }
        }

        // 接收完成时处理函数
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            //Console.WriteLine("ProcessReceive");
            // 检查远程主机是否关闭连接
            if (e.BytesTransferred > 0)
            {
                if (e.SocketError == SocketError.Success)
                {
                    Socket s = e.AcceptSocket;
                    //判断所有需接收的数据是否已经完成
                    //Console.WriteLine("ProcessReceive:Available: {0}", s.Available);
                    //Console.WriteLine("ProcessReceive:e: {0}", e.GetHashCode());
                    if (s.Available == 0)
                    {
                        // 处理接收到的数据
                        MessageUserToken messageUserToken = (MessageUserToken)e.UserToken;
                        messageUserToken.ProcessBuffer(e);
                    }
     
                    //为接收下一段数据，投递接收请求，这个函数有可能同步完成，这时返回false，并且不会引发SocketAsyncEventArgs.Completed事件
                    else if (!s.ReceiveAsync(e))
                    {
                        // 同步接收时处理接收完成事件
                        this.ProcessReceive(e);
                    }
                }
                else
                {
                    this.ProcessError(e);
                }
            }
            else
            {
                this.CloseClientSocket(e);
            }
        }

        // 发送完成时处理函数
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            //Console.WriteLine("ProcessSend:e: {0}", e.GetHashCode());
            if (e.SocketError == SocketError.Success)
            {
                Socket s = e.AcceptSocket;

                //接收时根据接收的字节数收缩了缓冲区的大小，因此投递接收请求时，恢复缓冲区大小
                e.SetBuffer(0, bufferSize);
                if (!s.ReceiveAsync(e))     //投递接收请求
                {
                    // 同步接收时处理接收完成事件
                    this.ProcessReceive(e);
                }
            }
            else
            {
                this.ProcessError(e);
            }
        }

        // 处理socket错误
        private void ProcessError(SocketAsyncEventArgs e)
        {
            Socket s = e.AcceptSocket;
            IPEndPoint localEndPoint = s.LocalEndPoint as IPEndPoint;

            this.CloseClientSocket(s, e);
            Console.WriteLine("[Server] Socket error {0} on endpoint {1} during {2}.", (Int32)e.SocketError, localEndPoint, e.LastOperation);
        }

        // 关闭socket连接
        private void CloseClientSocket(SocketAsyncEventArgs e)
        {
            Socket s = e.AcceptSocket;
            this.CloseClientSocket(s, e);
        }

        // 关闭socket连接
        private void CloseClientSocket(Socket s, SocketAsyncEventArgs e)
        {
            //Console.WriteLine("CloseClientSocket");
            Interlocked.Decrement(ref this.numConnectedSockets);

            // SocketAsyncEventArg 对象被释放，压入可重用队列。
            e.AcceptSocket = null;
            ((MessageUserToken)e.UserToken).Reset();
            eventArgsPool.Push(e);
            Console.WriteLine("[Server] A client has been disconnected from the server. There are {0} clients connected to the server", this.numConnectedSockets);
            
            try
            {
                s.Shutdown(SocketShutdown.Send);
            }
            catch (Exception)
            {
                // Throw if client has closed, so it is not necessary to catch.
            }
            finally
            {
                s.Close();
            }
        }

        // accept 操作完成时回调函数
        private void OnAcceptCompleted(object sender, SocketAsyncEventArgs e)
        {
            this.ProcessAccept(e);
        }

        // 监听Socket接受处理
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            //Console.WriteLine("ProcessAccept");
            Socket s = e.AcceptSocket;
            if (s.Connected)
            {
                try
                {
                    SocketAsyncEventArgs eventArg = this.eventArgsPool.Pop();
                    //Console.WriteLine("ProcessAccept:eventArg: {0}", eventArg.GetHashCode());
                    if (eventArg != null)
                    {
                        // 从接受的客户端连接中取数据配置ioContext
                        eventArg.AcceptSocket = e.AcceptSocket;

                        Interlocked.Increment(ref this.numConnectedSockets);
                        Console.WriteLine("[Server] Client connection accepted. There are {0} clients connected to the server", this.numConnectedSockets);

                        if (!s.ReceiveAsync(eventArg))
                        {
                            this.ProcessReceive(eventArg);
                        }
                    }
                    else
                    {
                        //已经达到最大客户连接数量，在这接受连接，发送“连接已经达到最大数”，然后断开连接
                        //s.Send(Encoding.Default.GetBytes("连接已经达到最大数!"));
                        Console.WriteLine("[Server] Max client connections");
                        s.Close();
                    }
                }
                catch (SocketException ex)
                {
                    Socket token = e.AcceptSocket;
                    Console.WriteLine("[Server] Error when processing data received from {0}: {1}", token.RemoteEndPoint, ex.ToString());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Exception: {0}", ex);
                }
                // 投递下一个接受请求
                this.StartAccept(e);
            }
        }

        // 从客户端开始接受一个连接操作
        private void StartAccept(SocketAsyncEventArgs acceptEventArg)
        {
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(OnAcceptCompleted);
                //Console.WriteLine("StartAccept:newEventArg: {0}", acceptEventArg.GetHashCode());

            }
            else
            {
                //Console.WriteLine("StartAccept:eventArg: {0}", acceptEventArg.GetHashCode());
                // 重用前进行对象清理
                acceptEventArg.AcceptSocket = null;
                //Console.WriteLine("Need to clean AcceptSocket?");
            }

            if (!this.listenSocket.AcceptAsync(acceptEventArg))
            {
                this.ProcessAccept(acceptEventArg);
            }
        }

    }
}
