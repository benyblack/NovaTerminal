namespace Ntilde.CommandAssist.Domain;

public interface ISecretsFilter
{
    RedactionResult Redact(string commandText);
}
