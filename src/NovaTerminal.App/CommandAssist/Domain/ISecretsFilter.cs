namespace NovaTerminal.CommandAssist.Domain;

public interface ISecretsFilter
{
    RedactionResult Redact(string commandText);
}
