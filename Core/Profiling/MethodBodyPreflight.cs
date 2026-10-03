#nullable disable

using System;
using System.Reflection;
using System.Reflection.Emit;

namespace ValheimProfiler.Core.Profiling;

/// <summary>Read-only IL inspection. Does not JIT, execute, patch or force-initialize the target.</summary>
internal static class MethodBodyPreflight
{
    private static readonly OpCode[] SingleByte = new OpCode[256];
    private static readonly OpCode[] DoubleByte = new OpCode[256];

    static MethodBodyPreflight()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(OpCode))
                continue;
            OpCode opcode = (OpCode)field.GetValue(null);
            ushort value = unchecked((ushort)opcode.Value);
            if (value < 256)
                SingleByte[value] = opcode;
            else if ((value & 0xff00) == 0xfe00)
                DoubleByte[value & 0xff] = opcode;
        }
    }

    internal static string GetUnsupportedReason(byte[] il)
    {
        bool hasReturn = false;
        int offset = 0;
        while (offset < il.Length)
        {
            int instructionOffset = offset;
            byte value = il[offset++];
            if (value == 0xfe && offset >= il.Length)
                return "IL preflight failed: truncated opcode.";
            OpCode opcode = value == 0xfe ? DoubleByte[il[offset++]] : SingleByte[value];
            if (opcode.Size == 0)
                return "IL preflight failed: unknown opcode at offset " + instructionOffset + ".";
            hasReturn |= opcode == OpCodes.Ret;

            int size;
            switch (opcode.OperandType)
            {
                case OperandType.InlineNone: size = 0; break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: size = 1; break;
                case OperandType.InlineVar: size = 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: size = 8; break;
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineI:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: size = 4; break;
                case OperandType.InlineSwitch:
                    if (il.Length - offset < 4)
                        return "IL preflight failed: truncated switch operand.";
                    int count = il[offset] | il[offset + 1] << 8 | il[offset + 2] << 16 | il[offset + 3] << 24;
                    if (count < 0 || count > (il.Length - offset - 4) / 4)
                        return "IL preflight failed: invalid switch operand.";
                    size = 4 + count * 4;
                    break;
                default:
                    return "IL preflight failed: unsupported operand encoding.";
            }
            if (size > il.Length - offset)
                return "IL preflight failed: truncated instruction operand.";
            offset += size;
        }

        // Mono/Harmony wrappers around throw-only bodies can introduce invalid unreachable IL.
        // Decode opcodes: a byte equal to ret inside a token or operand is not a return instruction.
        return hasReturn ? null : "The IL body has no return instruction (for example, a throw-only method); timing wrappers are not attempted on Mono.";
    }
}
