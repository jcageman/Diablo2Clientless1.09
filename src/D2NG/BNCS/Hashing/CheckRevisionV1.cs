using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;

namespace D2NG.BNCS.Hashing
{
    public static class CheckRevisionV1
    {
        public static uint FastComputeHash(string formula, string mpq, string game, string bnet, string d2)
        {
            return FastComputeHash(formula, FindConstant(File.ReadAllBytes(mpq), Path.GetFileName(mpq)),
                        File.ReadAllBytes(game), File.ReadAllBytes(bnet), File.ReadAllBytes(d2));
        }
        public static uint FastComputeHash(string formula, FileStream mpq, FileStream game, FileStream bnet, FileStream d2)
        {
            return FastComputeHash(formula, FindConstant(File.ReadAllBytes(mpq.Name), Path.GetFileName(mpq.Name)),
                        File.ReadAllBytes(game.Name), File.ReadAllBytes(bnet.Name), File.ReadAllBytes(d2.Name));
        }
        public static uint FastComputeHash(string formula, uint constant, byte[] game, byte[] bnet, byte[] d2)
        {
            var values = new uint[4];
            return FastComputeHashByValues(BuildFormula(formula, ref values), ref values, constant, game, bnet, d2);
        }

        public static uint ComputeHash(string formula, string mpq, string game, string bnet, string d2)
        {
            return ComputeHash(formula, FindConstant(File.ReadAllBytes(mpq), Path.GetFileName(mpq)),
                        File.ReadAllBytes(game), File.ReadAllBytes(bnet), File.ReadAllBytes(d2));
        }
        public static uint ComputeHash(string formula, FileStream mpq, FileStream game, FileStream bnet, FileStream d2)
        {
            return ComputeHash(formula, FindConstant(File.ReadAllBytes(mpq.Name), Path.GetFileName(mpq.Name)),
                        File.ReadAllBytes(game.Name), File.ReadAllBytes(bnet.Name), File.ReadAllBytes(d2.Name));
        }
        public static uint ComputeHash(string formula, uint constant, byte[] game, byte[] bnet, byte[] d2)
        {
            var values = new uint[4];
            return ComputeHashByValues(BuildFormula(formula, ref values), ref values, constant, game, bnet, d2);
        }

        public static uint FindConstant(byte[] mpq, string fileName)
        {
            uint constant = 0;
            fileName = fileName.Replace(".mpq", ".dll");

            using (var dll = new MemoryStream(mpq))
            {
                using (var arch = new MpqLib.MpqArchive(dll))
                {
                    string listfile = Path.GetTempFileName();
                    using (var sw = new StreamWriter(File.OpenWrite(listfile)))
                        sw.WriteLine(fileName);
                    arch.ExternalListFile = listfile;
                    var f = arch.Files.FirstOrDefault((file) => file.Name == fileName);
                    using (var stream = arch.OpenFile(fileName))
                    {
                        byte[] bytes = new byte[f.UncompressedSize];
                        stream.BlockRead(bytes);
                        int index = -1;
                        for (int i = 0; i < bytes.Length; i++)
                        {
                            if ((bytes[i] == 0x81 && bytes[i + 1] == 0x30))
                            {
                                index = i + 2;
                                break;
                            }
                            else if ((bytes[i] == 0x81 && bytes[i + 1] == 0x75) && bytes[i + 2] == 0xCC)
                            {
                                index = i + 3;
                                break;
                            }
                        }
                        if (index != -1)
                        {
                            constant = BitConverter.ToUInt32(bytes, index);
                        }
                    }
                }
            }

            return constant;
        }

        #region utilities
        private static uint FastComputeHashByValues(IEnumerable<FormulaOp> ops, ref uint[] values, uint constant, byte[] game, byte[] bnclient, byte[] d2client)
        {
            values[0] ^= constant;

            var hashFile = BuildFileHasher(ops);

            hashFile(ref values[0], ref values[1], ref values[2], ref values[3], game);
            hashFile(ref values[0], ref values[1], ref values[2], ref values[3], bnclient);
            hashFile(ref values[0], ref values[1], ref values[2], ref values[3], d2client);

            return values[2];
        }
        private static FileHasher BuildFileHasher(IEnumerable<FormulaOp> ops)
        {
            var uintType = typeof(uint).MakeByRefType();
            var method = new DynamicMethod("HashFile", typeof(void), new[] { uintType, uintType, uintType, uintType, typeof(byte[]) });
            var touint32 = typeof(BitConverter).GetMethod("ToUInt32", new[] { typeof(byte[]), typeof(int) });

            var gen = method.GetILGenerator();

            var start = gen.DefineLabel();
            var index = gen.DeclareLocal(typeof(int));
            var len = gen.DeclareLocal(typeof(int));

            // initialize the loop counter
            gen.Emit(OpCodes.Ldc_I4_0);
            gen.Emit(OpCodes.Stloc, index);

            // load the length of the array into a local
            gen.Emit(OpCodes.Ldarg, (short)4);
            gen.Emit(OpCodes.Ldlen);
            gen.Emit(OpCodes.Conv_I4);
            gen.Emit(OpCodes.Stloc, len);

            // start of loop across the file
            gen.MarkLabel(start);

            // load the value of arg4 at index into the address of arg3
            gen.Emit(OpCodes.Ldarg_3);
            gen.Emit(OpCodes.Ldarg_S, (byte)4);
            gen.Emit(OpCodes.Ldloc, index);
            gen.EmitCall(OpCodes.Call, touint32, null);
            gen.Emit(OpCodes.Stind_I4);

            // for each op in the formula...
            foreach (var op in ops)
            {
                // load the result address
                gen.Emit(Ldargs[op.Res]);

                // load the first value
                gen.Emit(Ldargs[op.Var1]);
                gen.Emit(OpCodes.Ldind_U4);

                // load the second value
                gen.Emit(Ldargs[op.Var2]);
                gen.Emit(OpCodes.Ldind_U4);

                // execute the operator
                gen.Emit(Operators[op.Op]);

                // store the result in the result address
                gen.Emit(OpCodes.Stind_I4);
            }

            // increment the loop counter 
            gen.Emit(OpCodes.Ldloc, index);
            gen.Emit(OpCodes.Ldc_I4_4);
            gen.Emit(OpCodes.Add);
            gen.Emit(OpCodes.Stloc, index);

            // jump back to the top of the label if the loop counter is less arg4's length
            gen.Emit(OpCodes.Ldloc, index);
            gen.Emit(OpCodes.Ldloc, len);
            gen.Emit(OpCodes.Blt, start);
            gen.Emit(OpCodes.Ret);

            return (FileHasher)method.CreateDelegate(typeof(FileHasher));
        }

        private static uint ComputeHashByValues(IEnumerable<FormulaOp> ops, ref uint[] values, uint constant, byte[] game, byte[] bnclient, byte[] d2client)
        {
            values[0] ^= constant;

            ComputeFileHash(ops, game, ref values);
            ComputeFileHash(ops, bnclient, ref values);
            ComputeFileHash(ops, d2client, ref values);

            return values[2];
        }
        private static void ComputeFileHash(IEnumerable<FormulaOp> formula, byte[] file, ref uint[] values)
        {
            var len = file.Length;
            for (var i = 0; i < len; i += 4)
            {
                values[3] = BitConverter.ToUInt32(file, i);
                foreach (var op in formula)
                    values[op.Res] = Operations[op.Op](values[op.Var1], values[op.Var2]);
            }
        }

        private static IEnumerable<FormulaOp> BuildFormula(string formula, ref uint[] values)
        {
            var ops = new List<FormulaOp>();
            string[] tokens = formula.Split(' ');
            foreach (string token in tokens)
            {
                var param = token.Split('=');

                if (param.Length == 1)
                    continue;

                var res = WhichVariable(param[0][0]);
                if (char.IsDigit(param[1][0]))
                    values[res] = Convert.ToUInt32(param[1]);
                else
                {
                    var method = param[1];
                    ops.Add(new FormulaOp(method[1], res, WhichVariable(method[0]), WhichVariable(method[2])));
                }
            }
            return ops;
        }
        private static int WhichVariable(char param)
        {
            int res = (param) - 'A';
            if (res > 2) res = 3;
            return res;
        }
        #endregion

        #region operations
        private static uint Add(uint var1, uint var2) { return var1 + var2; }
        private static uint Subtract(uint var1, uint var2) { return var1 - var2; }
        private static uint Multiply(uint var1, uint var2) { return var1 * var2; }
        private static uint Divide(uint var1, uint var2) { return var1 / var2; }
        private static uint Or(uint var1, uint var2) { return var1 | var2; }
        private static uint Xor(uint var1, uint var2) { return var1 ^ var2; }
        private static uint And(uint var1, uint var2) { return var1 & var2; }
        #endregion

        #region constants
        private static readonly Dictionary<char, Operator> Operations = new Dictionary<char, Operator> {
            {'+', Add}, {'-', Subtract}, {'*', Multiply}, {'/', Divide}, {'|', Or}, {'&', And}, {'^', Xor}
        };

        private static readonly Dictionary<int, OpCode> Ldargs = new Dictionary<int, OpCode> {
            {0, OpCodes.Ldarg_0}, {1, OpCodes.Ldarg_1}, {2, OpCodes.Ldarg_2}, {3, OpCodes.Ldarg_3}
        };

        private static readonly Dictionary<char, OpCode> Operators = new Dictionary<char, OpCode> {
            {'+', OpCodes.Add}, {'-', OpCodes.Sub}, {'*', OpCodes.Mul}, {'/', OpCodes.Div},
            {'|', OpCodes.Or}, {'&', OpCodes.And}, {'^', OpCodes.Xor}
        };
        #endregion

        private delegate void FileHasher(ref uint a, ref uint b, ref uint c, ref uint s, byte[] f);
        private delegate uint Operator(uint var1, uint var2);

        private sealed class FormulaOp
        {
            public FormulaOp(char op, int res, int var1, int var2) { Res = res; Var1 = var1; Var2 = var2; Op = op; }

            public int Var1 { get; private set; }
            public int Var2 { get; private set; }
            public int Res { get; private set; }
            public char Op { get; private set; }
        }
    }
}
