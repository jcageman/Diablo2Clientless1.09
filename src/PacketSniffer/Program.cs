using D2NG.Core.BNCS;
using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.MCP;
using Serilog;
using Serilog.Events;
using SharpPcap;
using System;
using System.Collections.Concurrent;
using System.IO;

namespace PacketSniffer;

class Program
{
    public static ConcurrentDictionary<int, GameServerConnection> gameServerConnections = new();

    public static BncsConnection bncsConnection = new();

    public static McpConnection mcpConnection = new();
    static void Main(string[] args)
    {
        bncsConnection._stream = new SnifferNetworkStream(new byte[] { });
        bncsConnection.PacketReceived += (obj, eventArgs) =>
        {
            IncomingBNCSPackets.HandleIncomingPacket(eventArgs);
        };
        mcpConnection._stream = new SnifferNetworkStream(new byte[] { });
        mcpConnection.PacketReceived += (obj, eventArgs) =>
        {
            IncomingMCPPackets.HandleIncomingPacket(eventArgs);
        };
        File.Delete("log.txt");

        Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Verbose()
        .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
        .WriteTo.File("log.txt")
        .CreateLogger();



        // Retrieve the device list from the local machine
        var allDevices = CaptureDeviceList.Instance;

        if (allDevices.Count == 0)
        {
            Console.WriteLine("No interfaces found! Make sure WinPcap is installed.");
            return;
        }

        var selectedDevice = allDevices.Count == 1 ? allDevices[0] : null;
        if (selectedDevice == null)
        {
            // Print the list
            for (int i = 0; i != allDevices.Count; ++i)
            {
                var device = allDevices[i];
                Console.Write((i + 1) + ". " + device.Name);
                if (device.Description != null)
                    Console.WriteLine(" (" + device.Description + ")");
                else
                    Console.WriteLine(" (No description available)");
            }

            int deviceIndex = 0;
            do
            {
                Console.WriteLine("Enter the interface number (1-" + allDevices.Count + "):");
                string deviceIndexString = Console.ReadLine();
                if (!int.TryParse(deviceIndexString, out deviceIndex) ||
                    deviceIndex < 1 || deviceIndex > allDevices.Count)
                {
                    deviceIndex = 0;
                }
            } while (deviceIndex == 0);

            // Take the selected adapter
            selectedDevice = allDevices[deviceIndex - 1];
        }


        //Register our handler function to the 'packet arrival' event

        selectedDevice.OnPacketArrival +=

            new PacketArrivalEventHandler(PacketHandler);



        //Open the device for capturing

        int readTimeoutMilliseconds = 1000;
        var deviceConfiguration = new DeviceConfiguration
        {
            Mode = DeviceModes.Promiscuous,
            ReadTimeout = readTimeoutMilliseconds
        };
        selectedDevice.Open(deviceConfiguration);
        selectedDevice.Filter = "tcp port 4000 or 6112 or 6113";
        selectedDevice.Capture();
        selectedDevice.Close();
    }

    // Callback function invoked by Pcap.Net for every incoming packet
    private static void PacketHandler(object sender, PacketCapture packetCapture)
    {
        var rawPacket = packetCapture.GetPacket();
        var packet = PacketDotNet.Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
        var tcpPacket = packet.Extract<PacketDotNet.TcpPacket>();
        if (tcpPacket != null && tcpPacket.PayloadData.Length > 0)
        {
            var bytes = tcpPacket.PayloadData;
            if (tcpPacket.SourcePort == 4000)
            {
                if(!gameServerConnections.TryGetValue(tcpPacket.DestinationPort, out var gameServerConnection))
                {
                    gameServerConnection = new GameServerConnection(gameServerConnections.Count);
                    gameServerConnection._stream = new SnifferNetworkStream(new byte[] { });
                    gameServerConnection.PacketReceived += (obj, eventArgs) =>
                    {
                        IncomingD2GSPackets.HandleIncomingPacket(eventArgs);
                    };
                    gameServerConnections.TryAdd(tcpPacket.DestinationPort, gameServerConnection);
                }

                if (bytes.Length == 2 && bytes[0] == 0xA7 && bytes[1] == 0x01)
                {
                    Log.Debug($"D2GS Initial packet received: {bytes.ByteArrayToString()}");
                    return;
                }
                Log.Debug($"D2GS Full packet received: {bytes.ByteArrayToString()}");
                var stream = gameServerConnection._stream as SnifferNetworkStream;
                stream.AddBytes(bytes);
                var initialBytes = stream.GetBytes();
                try
                {
                    initialBytes = stream.GetBytes();
                    while (gameServerConnection.ReadPacket() != null)
                    {

                    }
                }
                catch
                {
                    stream.SetBytes(initialBytes);
                }
            }
            else if(tcpPacket.DestinationPort == 4000)
            {
                OutgoingD2GSPackets.HandleOutgoingPacket(bytes);
            }
            else if (tcpPacket.SourcePort == 6112)
            {
                Log.Debug($"BNCS Full packet received: {bytes.ByteArrayToString()}");
                var stream = bncsConnection._stream as SnifferNetworkStream;
                stream.AddBytes(bytes);
                try
                {
                    bncsConnection.ReadPacket();
                }
                catch
                {

                }
            }
            else if (tcpPacket.DestinationPort == 6112)
            {
                OutgoingBNCSPackets.HandleOutgoingPacket(bytes);
            }
            else if (tcpPacket.SourcePort == 6113)
            {
                if (bytes.Length == 7 && bytes[0] == 0x07 && bytes[2] == 0x01)
                {
                    Log.Debug($"MCP Initial packet received: {bytes.ByteArrayToString()}");
                    return;
                }

                Log.Debug($"MCP Full packet received: {bytes.ByteArrayToString()}");
                var stream = mcpConnection._stream as SnifferNetworkStream;
                stream.AddBytes(bytes);
                mcpConnection.ReadPacket();
            }
            else if (tcpPacket.DestinationPort == 6113)
            {
                OutgoingMCPPackets.HandleOutgoingPacket(bytes);
            }
        }
    }
}
