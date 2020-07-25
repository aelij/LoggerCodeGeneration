using System;

namespace Arbel.Extensions.Logging.CodeGeneration
{
    internal sealed class Converter
    {
        public Delegate Func { get; }
        public int Index { get; }

        public Converter(Delegate func, int index)
        {
            Func = func;
            Index = index;
        }
    }
}