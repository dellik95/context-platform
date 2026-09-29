const { env } = require('process');

// Context.Dashboard.Server has no HTTPS listener configured (ContextHostOptions/Program.cs bind
// plain HTTP only — see BindHost's remarks), unlike the `dotnet new angular` template this proxy
// shape is copied from, so there's no ASPNETCORE_HTTPS_PORT fallback to check here.
const target = env.ASPNETCORE_URLS ? env.ASPNETCORE_URLS.split(';')[0] : 'http://127.0.0.1:47923';

const PROXY_CONFIG = [
  {
    context: ['/api', '/mcp', '/health'],
    target,
    secure: false,
  },
  {
    context: ['/hubs'],
    target,
    secure: false,
    ws: true,
  },
];

module.exports = PROXY_CONFIG;
