﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Networking.Communication
{
    public class CommunicatorClient : ICommunicator
    {
        private TcpClient client;
        private Dictionary<string, INotificationHandler> handlers = new();
        public string? _myIP;
        public string? _myPort;

        public string Start(string serverIP = null, string serverPort = null)
        {
            try
            {
                client = new TcpClient(serverIP, int.Parse(serverPort));
                ThreadPool.QueueUserWorkItem(ReceiveData);

                IPEndPoint? localEndPoint = (IPEndPoint?)client.Client.LocalEndPoint;
                if (localEndPoint != null)
                {
                    string remoteIpAddress = localEndPoint.Address.MapToIPv4().ToString();
                    int remotePort = localEndPoint.Port;
                    _myIP = remoteIpAddress;
                    _myPort = remotePort.ToString();
                }

                return "success";
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to connect: {e.Message}");
                return "failure";
            }
        }

        /// <summary>
        /// Receives data from clients and notifies handlers
        /// </summary>
        /// <param name="state"></param>
        private void ReceiveData(object state)
        {
            while (true)
            {
                NetworkStream stream = client.GetStream();
                
                byte[] buflen = new byte[4];
                int bytesRead = stream.Read(buflen, 0, buflen.Length);
                if (bytesRead == 0) break;

                int packetLength = BitConverter.ToInt32(buflen, 0);

                byte[] buffer = new byte[packetLength];
                bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                string receivedData = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                string[] packetParts = receivedData.Split(new[] { ':' }, 2);
                if (packetParts.Length == 2)
                {
                    string module = packetParts[0];
                    string data = packetParts[1];

                    if (handlers.TryGetValue(module, out INotificationHandler handler))
                    {
                        handler.OnDataReceived(data);
                    }
                }
            }
        }

        /// <summary>
        /// Sends data to clients
        /// If destination is null, broadcast to all clients
        /// </summary>
        /// <param name="serializedData">data as serialized string</param>
        /// <param name="moduleOfPacket">module to which the data belongs</param>
        /// <param name="destination">null here as data sent to server</param>
        /// <returns></returns>
        public void Send(string serializedData, string moduleOfPacket, string? destination)
        {
            string packet = $"{moduleOfPacket}:{serializedData}";
            byte[] buflen = BitConverter.GetBytes(packet.Length);
            byte[] buffer = Encoding.UTF8.GetBytes(packet);
            client.GetStream().Write(buflen, 0, buflen.Length);
            client.GetStream().Write(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Subscribes to a module
        /// </summary>
        /// <param name="moduleName">module to subscribe to</param>
        /// <param name="notificationHandler">handler to notify when data is received</param>
        /// <param name="isHighPriority">whether the handler should be notified first</param>
        public void Subscribe(string moduleName, INotificationHandler notificationHandler, bool isHighPriority = false)
        {
            handlers[moduleName] = notificationHandler;
        }

        public void Stop()
        {
            client.Close();
        }

        public void AddClient(string clientId, TcpClient socket) { }

        public void RemoveClient(string clientId) { }

        public Dictionary<string, TcpClient> GetClientList() { return null; }
        public string GetMyIP()
        {
            return _myIP;
        }

        public string GetMyPort()
        {
            return _myPort;
        }
    }
}
