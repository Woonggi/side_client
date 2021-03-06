﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System;

public class Client : MonoBehaviour
{
    public static Client instance;
    public static int dataBufferSize = 4096 * 1024;

    public string ip;
    public int port = 26950;
    public int myId = 0;
    public TCP tcp;
    public UDP udp;

    [HideInInspector]
    public bool isConnected = false;
    private delegate void PacketHandler(CustomPacket packet);
    private static Dictionary<int, PacketHandler> packetHandlers;

    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
        }
        else if (instance != this)
        {
            Debug.Log("Instance already exists, destroying object!");
            Destroy(this);
        }
    }

    private void Start()
    {
        tcp = new TCP();
        udp = new UDP();
    }

    private void OnApplicationQuit()
    {
        Disconnect();
    }

    public bool ConnectToServer(string hostname)
    {
        if (tcp.Connect(hostname) == true)
        { 
            InitializeClientData();
            isConnected = true;
            return true;
        }
        else
        {
            UIManager.instance.FailedConnectionMessage(hostname);
        }
        return false; 
    }

    public class TCP
    {
        public TcpClient socket;

        private NetworkStream stream;
        private CustomPacket receivedData;
        private byte[] receiveBuffer;

        public bool Connect(string hostname)
        {
            try
            {
                socket = new TcpClient
                {
                    ReceiveBufferSize = dataBufferSize,
                    SendBufferSize = dataBufferSize
                };
                IPAddress[] addrList = Dns.GetHostAddresses(hostname);
                foreach (IPAddress addr in addrList)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        instance.ip = addr.ToString();
                        break;
                    }
                }
                Debug.Log($"TCP = {instance.ip}");
                receiveBuffer = new byte[dataBufferSize];
                socket.BeginConnect(instance.ip, instance.port, ConnectCallback, socket);
            }
            catch (Exception e)
            {
                Debug.Log(e.ToString());
                return false;
            }
            return true;
        }
        private void ConnectCallback(IAsyncResult result)
        {
            socket.EndConnect(result);

            if (!socket.Connected)
            {
                return;
            }
            stream = socket.GetStream();
            receivedData = new CustomPacket();
            stream.BeginRead(receiveBuffer, 0, dataBufferSize, ReceiveCallback, null);
        }

        public void SendData(CustomPacket packet)
        {
            try
            {
                if (socket != null)
                {
                    stream.BeginWrite(packet.ToArray(), 0, packet.Length(), null, null);
                }
            }
            catch(Exception ex)
            {
                Debug.Log($"Error sending data to server via TCP : {ex}");
            }
        }

        private void ReceiveCallback(IAsyncResult result)
        {
            try
            {
                int byteLength = stream.EndRead(result);
                if (byteLength <= 0)
                {
                    instance.Disconnect();
                    return;
                }
                byte[] data = new byte[byteLength];
                Array.Copy(receiveBuffer, data, byteLength);

                receivedData.Reset(HandleData(data)); 
                stream.BeginRead(receiveBuffer, 0, dataBufferSize, ReceiveCallback, null);
            }
            catch
            {
                Disconnect();
            }
        }

        private bool HandleData(byte[] data)
        {
            int packetLength = 0;

            receivedData.SetBytes(data);

            if (receivedData.UnreadLength() >= 4)
            {
                packetLength = receivedData.ReadInt();
                if (packetLength <= 0)
                {
                    return true; // Read done! reset.
                }
            }

            while (packetLength > 0 && packetLength <= receivedData.UnreadLength())
            {
                byte[] packetBytes = receivedData.ReadBytes(packetLength);
                ThreadManager.ExecuteOnMainThread(() =>
                {
                    using (CustomPacket packet = new CustomPacket(packetBytes))
                    {
                        int packetId = packet.ReadInt();
                        packetHandlers[packetId](packet);
                    }
                });
                packetLength = 0;
                if (receivedData.UnreadLength() >= 4)
                {
                    packetLength = receivedData.ReadInt();
                    if (packetLength <= 0)
                    {
                        return true; // Read done! reset.
                    }
                }
            }

            if (packetLength <= 1)
            {
                return true;
            }
            return false;
        }
        private void Disconnect()
        {
            instance.Disconnect();
            stream = null;
            receivedData = null;
            receiveBuffer = null;
            socket = null;
        }
    }

    public class UDP
    {
        public UdpClient socket;
        public IPEndPoint endPoint;
        public UDP()
        {
        }
        
        public void Connect (int localPort)
        {
            endPoint = new IPEndPoint(IPAddress.Parse(instance.ip), instance.port);
            socket = new UdpClient(localPort);
            socket.Connect(endPoint);
            socket.BeginReceive(ReceiveCallback, null);

            using (CustomPacket packet = new CustomPacket())
            {
                SendData(packet);
            }
        }

        public void SendData(CustomPacket packet)
        {
            try
            {
                // To specify which user's sending the packet
                packet.InsertInt(instance.myId);
                if (socket != null)
                {
                    socket.BeginSend(packet.ToArray(), packet.Length(), null, null);
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"Error Sending data to server via UDP {ex}");
            }
        }

        private void ReceiveCallback(IAsyncResult result)
        {
            try
            {
                byte[] data = socket.EndReceive(result, ref endPoint);
                socket.BeginReceive(ReceiveCallback, null);

                if (data.Length < 4)
                {
                    instance.Disconnect();
                    return;
                }

                HandleData(data);
            }
            catch 
            {
                Disconnect();
            }
        }

        private void HandleData(byte[] data)
        {
            using (CustomPacket packet = new CustomPacket(data))
            {
                int packetLength = packet.ReadInt();
                data = packet.ReadBytes(packetLength);
            }

            ThreadManager.ExecuteOnMainThread(() =>
            {
                using (CustomPacket packet = new CustomPacket(data))
                {
                    int packetId = packet.ReadInt();
                    packetHandlers[packetId](packet);
                }
            });
        }

        private void Disconnect()
        {
            instance.Disconnect();
            endPoint = null;
            socket = null;
        }
    }

    private void InitializeClientData()
    {
        packetHandlers = new Dictionary<int, PacketHandler>()
        {
            { (int)ServerPackets.SP_WELCOME, ClientHandle.Welcome },
            { (int)ServerPackets.SP_SPAWN_PLAYER, ClientHandle.SpawnPlayer },
            { (int)ServerPackets.SP_PLAYER_POSITION, ClientHandle.PlayerPosition },
            { (int)ServerPackets.SP_PLAYER_ROTATION, ClientHandle.PlayerRotation },
            { (int)ServerPackets.SP_PLAYER_SHOOTING, ClientHandle.PlayerShooting },
            { (int)ServerPackets.SP_BULLET_POSITION, ClientHandle.BulletPosition },
            { (int)ServerPackets.SP_BULLET_DESTROY, ClientHandle.BulletDestroy},
            { (int)ServerPackets.SP_PLAYER_DISCONNECTED, ClientHandle.PlayerDisconnected },
            { (int)ServerPackets.SP_PLAYER_HIT, ClientHandle.PlayerHit },
            { (int)ServerPackets.SP_PLAYER_DESTROY, ClientHandle.PlayerDestroy },
            { (int)ServerPackets.SP_PLAYER_RESPAWN, ClientHandle.PlayerRespawn },
            { (int)ServerPackets.SP_SPAWN_POWERUP, ClientHandle.SpawnPowerUp},
            { (int)ServerPackets.SP_PLAYER_POWERUP, ClientHandle.PowerUp },
            { (int)ServerPackets.SP_ASTEROID_SPAWN, ClientHandle.SpawnAsteroid },
            { (int)ServerPackets.SP_ASTEROID_MOVEMENT, ClientHandle.AsteroidPosition },
            { (int)ServerPackets.SP_ASTEROID_DESTROY, ClientHandle.AsteroidDestroy },
            { (int)ServerPackets.SP_GAME_OVER, ClientHandle.GameOver }
        };
        Debug.Log("Initialized packets.");
    }
    private void Disconnect()
    {
        if (isConnected == true)
        {
            isConnected = false;
            tcp.socket.Close();
            udp.socket.Close();
            Debug.Log("Disconnected from server.");
        }
    }

}
