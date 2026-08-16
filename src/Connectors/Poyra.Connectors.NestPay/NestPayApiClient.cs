using System.Text;
using System.Xml.Linq;
using Poyra.Connectors.Abstractions;

namespace Poyra.Connectors.NestPay;

/// <summary>
/// NestPay XML API (/fim/api) istemcisi — void (Type=Void), iade (Type=Credit), durum (ORDERSTATUS).
/// API kullanıcısı, 3D store kullanıcısından ayrıdır (api_name/api_password kimlik alanları).
/// </summary>
public sealed class NestPayApiClient(HttpClient httpClient)
{
    public async Task<ConnectorOperationResult> SendAsync(
        string gatewayBase, ConnectorCredentials credentials, XElement request, CancellationToken ct)
    {
        var envelope = new XElement("CC5Request",
            new XElement("Name", credentials.Require("api_name")),
            new XElement("Password", credentials.Require("api_password")),
            new XElement("ClientId", credentials.Require("client_id")));
        envelope.Add(request.Elements());

        using var content = new StringContent(
            "DATA=" + Uri.EscapeDataString(envelope.ToString(SaveOptions.DisableFormatting)),
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync($"{gatewayBase.TrimEnd('/')}/fim/api", content, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnavailableException($"NestPay API'ye ulaşılamadı: {ex.Message}", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ConnectorUnavailableException($"NestPay API HTTP {(int)response.StatusCode}");

        return ParseResponse(body);
    }

    private static ConnectorOperationResult ParseResponse(string xml)
    {
        XElement root;
        try
        {
            root = XElement.Parse(xml);
        }
        catch (Exception)
        {
            return ConnectorOperationResult.Fail(UnifiedErrors.ProcessingError, null, "Geçersiz API yanıtı");
        }

        var code = root.Element("ProcReturnCode")?.Value;
        var message = root.Element("ErrMsg")?.Value ?? root.Element("Response")?.Value;
        var transId = root.Element("TransId")?.Value;

        return code == "00"
            ? ConnectorOperationResult.Ok(transId)
            : ConnectorOperationResult.Fail(NestPayErrorMap.ToUnified(code), code, message);
    }
}
