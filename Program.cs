using chat.Components;
using chat.Session;
using XmppDotNet;
using XmppDotNet.Transport;
using XmppDotNet.Transport.WebSocket;
using System.Reactive.Linq;

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

            builder.Services.AddScoped<XmppClient>(sp =>
            {
                var client = new XmppClient(conf =>
                {
                    var alamatOpenfireLocal = new Uri("ws://sensor:sensor/ws/");
                    conf.UseWebSocketTransport(new StaticNameResolver(alamatOpenfireLocal));
                    conf.AutoReconnect = true;
                });
                return client;

            });

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
