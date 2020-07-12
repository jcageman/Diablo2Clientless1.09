using D2NG.D2GS;
using D2NG.D2GS.Helpers;
using Serilog;
using Serilog.Events;
using SharpPcap;
using System;
using System.IO;

namespace PacketSniffer
{
    class Program
    {
        public static GameServerConnection gameServerConnection = new GameServerConnection();
        static void Main(string[] args)
        {
            File.Delete("log.txt");

            Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
            .WriteTo.File("log.txt")
            .CreateLogger();

            gameServerConnection.PacketReceived += (obj, eventArgs) =>
            {
                IncomingPackets.HandleIncomingPacket(eventArgs);
            };

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

            selectedDevice.Open(DeviceMode.Promiscuous, readTimeoutMilliseconds);
            selectedDevice.Filter = "tcp port 4000";
            selectedDevice.Capture();
            selectedDevice.Close();
        }

        // Callback function invoked by Pcap.Net for every incoming packet
        private static void PacketHandler(object sender, CaptureEventArgs e)
        {
            var packet = PacketDotNet.Packet.ParsePacket(e.Packet.LinkLayerType, e.Packet.Data);
            var tcpPacket = packet.Extract<PacketDotNet.TcpPacket>();
            if (tcpPacket != null && tcpPacket.PayloadData.Length > 0)
            {
                var bytes = tcpPacket.PayloadData;
                if (tcpPacket.SourcePort == 4000)
                {
                    if (bytes.Length == 2 && bytes[0] == 0xA7 && bytes[1] == 0x01)
                    {
                        Log.Debug($"Initial packet received: {bytes.ByteArrayToString()}");
                        return;
                    }
                    Log.Debug($"Full packet received: {bytes.ByteArrayToString()}");
                    gameServerConnection._stream = new SnifferNetworkStream(bytes);
                    gameServerConnection.ReadPacket();
                }
                else
                {
                    OutgoingPackets.HandleOutgoingPacket(bytes);
                }
            }
        }
    }
}
