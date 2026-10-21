using chat.Components;
using chat.Services;
using chat.Session;

namespace chat
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddOptions<XmppOptions>()
                .Bind(builder.Configuration.GetSection(XmppOptions.SectionName))
                .Validate(o => Uri.TryCreate(o.WebSocketUri, UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss",
                    "Xmpp:WebSocketUri in appsettings.json must be a ws:// or wss:// URL, e.g. ws://localhost:7070/ws/")
                .Validate(o => !string.IsNullOrWhiteSpace(o.Domain),
                    "Xmpp:Domain in appsettings.json is required, e.g. openfire")
                .ValidateOnStart();

            // Scoped = one XMPP connection per Blazor circuit (browser tab), closed when the circuit ends.
            builder.Services.AddScoped<XmppClientService>();

            builder.Services.AddScoped<UserSession>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
