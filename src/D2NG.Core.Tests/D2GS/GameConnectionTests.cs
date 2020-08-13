using D2NG.Core.D2GS;
using D2NG.Core.D2GS.Helpers;
using D2NG.Core.D2GS.NetworkStream;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace D2NG.Core.Tests.D2GS
{
    public class GameConnectionTests
    {
        private GameServerConnection CreateGameServerConnection(string packet)
        {
            var byteArray = packet.StringToByteArray();
            Mock<INetworkStream> mockedStream = new Mock<INetworkStream>();
            mockedStream.Setup(x => x.ReadByte()).Returns(() =>
            {
                if (byteArray.Length == 0)
                {
                    return -1;
                }

                var firstByte = byteArray[0];
                byteArray = byteArray.Skip(1).ToArray();
                return firstByte;
            });
            mockedStream.Setup(x => x.Read(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>())).Returns((byte[] buffer, int offset, int size) =>
            {
                var readSize = Math.Min(byteArray.Length, size);
                Array.Copy(byteArray, 0, buffer, offset, size);
                byteArray = byteArray.Skip(size).ToArray();
                return readSize;
            });

            var gameServerConnection = new GameServerConnection();
            gameServerConnection._stream = mockedStream.Object;
            return gameServerConnection;
        }

        private void GameServerConnectionBaseTest(string hexString, List<byte> expected)
        {
            var gameServerConnection = CreateGameServerConnection(hexString);

            var packetTypes = new List<byte>();
            gameServerConnection.PacketReceived += (obj, eventArgs) =>
            {
                packetTypes.Add(eventArgs.Type);
            };

            gameServerConnection.ReadPacket();
            var printableString = packetTypes.ToPrintString();
            Assert.Equal(expected, packetTypes);
        }

        [Fact]
        public void GameServerConnection1()
        {
            string hexString = "c6175fae340d2b0641a0b2328d7bf90fecdfabfe37df81146ff77baf70977099709b709d73f70e60f5f84db83787307afc25dc1bc38bbf15371a438bbf1538d21c5df8a9b8d21c5df8a9c690e2efc54dc690eafe2a5b6db6c6177777777777777777777777777777777777710c9487ffffffffffffffffffffffffe1f7fffffffffffffffffffffff98df8c4fc0a919a347e69206e8eb509b9f5c0a919a345f9a481ba35d6a1373eb8152334687f9a481ba340b509b9f5c0a919a3441cd240dd1a15a84dcfae";
            var expected = new List<byte> { 0x59, 0x13, 0x76, 0x94, 0x22, 0x22, 0x21, 0x21, 0x21, 0x21, 0x21, 0x23, 0x5E, 0x28, 0x29, 0x0B, 0x5F, 0x9C, 0x9C, 0x9C, 0x9C };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection2()
        {
            string hexString = "f13e02a81a34639a481bc6b148491250ae05503468879a481bca8040481250ae0590870c22f7e6b1e6e01a3505715108f2b4870a409c1c8ac45999cccce5c8e72b91ce4f999ca09cb1508a21283091d418c009c9939c99cba212d322119fe7e3613036006f20a530ec5b5ffd103b3ea903a2210f8c681d11087d5207440ecf8c681d10423e31a0744421f1a903a20767d520744108faa40e881d9f1a903a20847c6a40e9efdb4c9cd9c765a7a108e4f1735e4990e9901b2f232e6bcfd7e5547c09c2526486cb5d9747d5ff588f84a4c90d96b2e8fabfe5645c7f8484c211b2d7db6db6fffbffff65d1757fd622e12130846cb59745d5ff2aa1e0ee0dd31d1b2d765d0f57fd621e0dd31d1b2d65d0f57fcf1734e498434c686cbc8cb9a73f5f9e2e67c930729836365e465ccf9fafcf1723724c27260f4d9791972373f5f8";
            var expected = new List<byte> { 0x9C, 0x9C, 0x9D, 0x1D, 0x1D, 0x1D, 0x1D, 0x1D, 0x1D, 0x1E, 0x1E, 0x1E, 0x1D, 0x1D, 0x95, 0x03, 0x53, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x15, 0x7E, 0x51, 0x0E, 0x55, 0x13, 0x6D, 0x13, 0x56, 0x13, 0x6D, 0x13, 0x55, 0x13, 0x6D, 0x13, 0x51, 0x0E, 0x51, 0x0E, 0x51, 0x0E };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection3()
        {
            string hexString = "f1032ab1c14c2e260cc9d6bb2ec757dfac6385c4c1993ad65d8eafbf3c5d2f24c32a66d3af232e979fafcf1732e4988cca0365e465ccb9fafcf1731e385a6435275e465cc79f75f9e2e9392649657365e465d273f5f955071fa12d30aa6cb5d9741d5ff588384b4c2a9b2d65d0757fcf1730e498514c281b2f232e61cfd7e78ba3e4983b4c251b2f232e8f9fafcf1745c9308e985a365e465d173f5f954cb97a466539b2d765ccbabfeb0cb91994e6cb59732eaff958c78ff0b099a365afb6db6dfff7fffecb98f57fd618f0b099a365acb98f57fcaa93814a064906cb5d9749d5ff5893a064906cb59749d5ff2a9870f9c33263b365aecb98757fd61870cc98ecd968";
            var expected = new List<byte> { 0x55, 0x13, 0x6D, 0x13, 0x51, 0x0E, 0x51, 0x0E, 0x51, 0x0E, 0x51, 0x0E, 0x55, 0x13, 0x6D, 0x13, 0x51, 0x0E, 0x51, 0x0E, 0x51, 0x0E, 0x55, 0x13, 0x6D, 0x13, 0x56, 0x13, 0x6D, 0x13, 0x55, 0x13, 0x6D, 0x13, 0x55, 0x13, 0x6D };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection4()
        {
            string hexString = "f10465cc3abfe78b9b71e1d2321c9b678ba7e499149839365e465d3f3f5f9e2ed70f67d90e8db3c5d9e62e45a63636cf174dc9331648c6cbc8cba6e7ebf66fd5ff3927968e4532d1c9939e2e878752632211b6563fc7f8674c50365afb6db6dfff7fffecb9feaffac3fc33a6281b2d65cff57fcf17638770c4985236cf1741c936d313cd97919741cfd7e555f829872c8b66cb5d975fabefd62fc39645b365acbafd5f7e567e3fc41644a365afb6db6dfff7fffecbbf57fd63f1059128d96b2efd5ff3c5cff16a999124db3c5d7e4991d90046cbc8cbafcfd7e78bbf24c376446365e465df9fafce66672e47395c8e727ccce504e58a84510941848ea0c6004e4c9ce4ce";
            var expected = new List<byte> { 0x13, 0x51, 0x51, 0x0E, 0x51, 0x51, 0x51, 0x0E, 0x13, 0x1D, 0x1D, 0x1D, 0x51, 0x56, 0x13, 0x6D, 0x13, 0x51, 0x51, 0x0E, 0x55, 0x13, 0x6D, 0x13, 0x56, 0x13, 0x6D, 0x13, 0x51, 0x51, 0x0E, 0x51, 0x0E, 0x1D, 0x1D, 0x1D, 0x1D, 0x1D, 0x1D, 0x1E, 0x1E, 0x1E, 0x1D, 0x1D };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection5()
        {
            string hexString = "6b0eaffb6db6d87177e2a71a438bbf15371a43abf8a96db6db15aeffc3abfedb6db61c5df8a9c690e2efc54dc691577fe1d5fc54b6db6da00eaffb6db6d87177e2a71a438bbf15371a43abf8a96db6db15b7fe2f8777eb8d034ac190682c8ca35ffbdb6ffc657f80d7edb6";
            var expected = new List<byte> { 0x23, 0x21, 0x21, 0x23, 0x48, 0x23, 0x21, 0x21, 0x47, 0x23, 0x04, 0x23, 0x21, 0x21, 0x23, 0x48, 0x5B, 0x65, 0x8D };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection6()
        {
            string hexString = "5a0f1057f78be3216962b1a158d8368d6320da348d63045c194f4b234ac194ec6d1a4ec535632160f4693b258961c1d80254ac6c3b52322c194ed58d876a8691b06d194b476348d076348dc359d940aa762d8ca358de328d621c";
            var expected = new List<byte> { 0x26 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection7()
        {
            string hexString = "161f1d275265dce19506cf45614a4ee7d9239b3d1580";
            var expected = new List<byte> { 0x8A, 0x67, 0x67 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection8()
        {
            string hexString = "14528fb84a4c646cf45614a0ee14130a66cf4560";
            var expected = new List<byte> { 0x67, 0x67 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection9()
        {
            string hexString = "0d1f1d275293baec9c1b3d1580";
            var expected = new List<byte> { 0x8A, 0x67 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection10()
        {
            string hexString = "09588f84a4c646cb40";
            var expected = new List<byte> { 0x6D };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection11()
        {
            string hexString = "0f1f1d275893aec9c1b2d105d27358";
            var expected = new List<byte> { 0x8A, 0x6D, 0x2C };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection12()
        {
            string hexString = "0958838504c299b2d0";
            var expected = new List<byte> { 0x6D };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection13()
        {
            string hexString = "041f1d27";
            var expected = new List<byte> { 0x8A };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection14()
        {
            string hexString = "0b2f0015a3b8f69005b690";
            var expected = new List<byte> { 0x96 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection15()
        {
            string hexString = "085865ce19506cb4";
            var expected = new List<byte> { 0x6D };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection16()
        {
            string hexString = "285261dc50360266af45614a2ee058d8091abd1585297bad3620cd5e8ac2932ee341b027357a2b00";
            var expected = new List<byte> { 0x67, 0x67, 0x67, 0x67 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection17()
        {
            string hexString = "ac52c77281b053357a2b01e20afe832b2b1b158da76348dc359d8e0329606c56368ca76320da7629ab190b07a3492c4b10cb234ac0e0eca832ac1956160651acec6d1a4ec671a558d076348dc359d96958d8591a46b2d1d96550c8340d0329e9d8da348d21c43c415fd062d0ca381d964691a06d194681b4ec641a0b476330ca320da370d6328d876382b3d3d3b2c0ca76591a562b1a149d8d8348d234070767676767676767676767676770";
            var expected = new List<byte> { 0x67, 0x26, 0x26 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection18()
        {
            string hexString = "a30f1057f4195958d8ac6d3b1a46e1acec70194b0362b1b4653b1906d3b14d58c8583d1a49625886591a56070765419560cab0b0328d676368d276338d2ac683b1a46e1acecb4ac6c2c8d235968ecb2a8641a068194f4ec6d1a4690e21e20afe83168651c0ecb2348d0368ca340da76320d05a3b198651906d1b86b1946c3b1c159e9e9d960653b2c8d2b158d0a4ec6c1a4691a0383b3b3b3b3b3b3b3b3b3b3b3b3b80";
            var expected = new List<byte> { 0x26, 0x26 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection19()
        {
            string hexString = "223D10FA4BF81D360FD12C2770566C2AA25A27927B8AC73D2DFB2074D842441D0820";
            var expected = new List<byte> { 0x6C, 0x6D, 0x3E, 0x0D };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection20()
        {
            string hexString = "7D13C38EE2B0DBC0ACD8D048E6876405C118462C05520128D08500159C1A08FE76B6E7A118B0154EC118242320239200AC396046F3B5B7018118B015463230B44640470E9102521D80264E3922874AB15FA3F324ECDF94FFB37E79FD9BF2A723F66FC5BFE6B7E5FC657E865D0B2622D90837070118520AA438C5240720";
            var expected = new List<byte> { 0x3E, 0x9C, 0x9C, 0x9C, 0x69, 0x13, 0x13, 0x13, 0x13, 0x11, 0x65, 0x95, 0x1A };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection21()
        {
            string hexString = "1F13CC3B8AC9B89E1E7715871E1FBAF0680E81EB6DB6D8CC0CB50461981900";
            var expected = new List<byte> { 0x3E, 0x3E, 0x2A, 0x1E };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection22()
        {
            string hexString = "2913D2771588DD60A9C109A92910EC96FD90406A1EA23E29337E53FECDF9E7F66FCA9C8FD9BF16FF80";
            var expected = new List<byte> { 0x3E, 0x6D, 0x0D, 0x13, 0x13, 0x13, 0x13 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection23()
        {
            string hexString = "4E141B5FB6D8E8817E27AFDC542B71D102FC3983D7E13A5606F106FD14F17071C9B2E9A9AA27164638B20E38F2FCA4328E2ACB234C0880D178C03484107A34811071C1EF232E0E39FBF18E2C838E";
            var expected = new List<byte> { 0x3F, 0x7C, 0x3E, 0x7C, 0x22, 0x2C, 0x51, 0x60, 0x82, 0x0E, 0x60 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection24()
        {
            string hexString = "481D102FC4F5FB8A859E3A205F87307AFC274B20DE20DFA29E2E28F26D6A60BC60E2C8C716451E3CBF290CA38AB2C8D31D22F180690820F4691D051E297232E28F3F7E31C5914780";
            var expected = new List<byte> { 0x7C, 0x3E, 0x7C, 0x22, 0x2C, 0x51, 0x60, 0x82, 0x0E, 0x60 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection25()
        {
            string hexString = "4D141B5FB6D8E8817E27AFDC542CF1D102FC3983D7E13A5906F106FD14F171CF9363E260B860E2C8C716473E3CBF290CA38AB2C8D31D22F180690820F4691D073E01791971CF9FBF18E2C8E7C0";
            var expected = new List<byte> { 0x3F, 0x7C, 0x3E, 0x7C, 0x22, 0x2C, 0x51, 0x60, 0x82, 0x0E, 0x60 };
            GameServerConnectionBaseTest(hexString, expected);
        }

        [Fact]
        public void GameServerConnection26()
        {
            string hexString = "303D0DFA4BF965381F40AC36E84B09C0F2040C337E53FECDF9E7F66FCA9C8FD9BF16FF9ADF97F3D39E92FE322703E800";
            var expected = new List<byte> { 0x6C, 0x69, 0x13, 0x13, 0x13, 0x13, 0x11, 0x6C };
            GameServerConnectionBaseTest(hexString, expected);
        }
    }
}
