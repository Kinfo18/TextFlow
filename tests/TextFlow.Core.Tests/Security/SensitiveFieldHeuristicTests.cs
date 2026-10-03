using TextFlow.Core.Security;

namespace TextFlow.Core.Tests.Security;

public sealed class SensitiveFieldHeuristicTests
{
    [Theory]
    [InlineData("Contraseña", null)]
    [InlineData("Introduce tu contrasena", null)]
    [InlineData("Password", null)]
    [InlineData(null, "password")]           // HTML id exposed as AutomationId (GitHub)
    [InlineData(null, "loginPassword")]
    [InlineData(null, "user_pwd")]
    [InlineData("Passcode", null)]
    [InlineData("Clave", null)]
    [InlineData("Código PIN", null)]
    [InlineData("Mot de passe", null)]
    [InlineData("Senha", null)]
    public void PasswordLikeLabels_AreSensitive(string? name, string? automationId)
    {
        Assert.True(SensitiveFieldHeuristic.LooksLikePassword(name, automationId));
    }

    [Theory]
    [InlineData("Usuario", "login_field")]
    [InlineData("Correo electrónico", "email")]
    [InlineData("Buscar", "q")]
    [InlineData("Spinner", null)]           // "pin" only as a whole word
    [InlineData("Pasaporte", null)]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void OrdinaryFields_AreNot(string? name, string? automationId)
    {
        Assert.False(SensitiveFieldHeuristic.LooksLikePassword(name, automationId));
    }
}
