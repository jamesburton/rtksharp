namespace RtkSharp.Core;

public interface IArgumentParser
{
    RtkArguments Parse(string[] args);
}
