namespace Toolbox.Tools.RttCli.Scripting;

/// <summary>Negative status ladder — csbindgen only generates bare-literal consts.</summary>
internal static unsafe partial class LuaNativeGen
{
    internal const int ErrInvalidHandle = -1;
    internal const int ErrBadArgument = -2;
    internal const int ErrInternal = -3;
}
