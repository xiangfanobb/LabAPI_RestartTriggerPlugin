using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Paths;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RestartTriggerPlugin
{
    /// <summary>
    /// 远程触发服务器重启的HTTP插件，支持鉴权、冷却时间控制（已移除Host白名单验证）。
    /// </summary>
    public class RestartTriggerPlugin : Plugin
    {
        private Config _config;
        private HttpListener _httpListener;
        private DateTime _lastTriggerTime = DateTime.MinValue;
        private CancellationTokenSource _cts;
        private readonly object _timeLock = new object();

        #region 插件核心属性
        public override string Name => "RestartTrigger";
        public override string Description => "远程触发服务器重启的HTTP插件，支持鉴权、冷却控制（无Host验证）";
        public override string Author => "LabApi";
        public override Version Version => new Version(1, 0, 0);
        public override Version RequiredApiVersion => new Version(LabApiProperties.CompiledVersion);
        public override bool IsTransparent => true;
        #endregion

        #region 插件生命周期
        public override void Enable()
        {
            try
            {
                _cts = new CancellationTokenSource();
                LoadConfigs();

                if (_config.ListenPort < 1 || _config.ListenPort > 65535)
                {
                    Logger.Error("[RestartTrigger] 配置端口非法（1-65535），使用默认端口8080");
                    _config.ListenPort = 8080;
                }

                _httpListener = new HttpListener();
                try
                {
                    _httpListener.Prefixes.Add($"http://*:{_config.ListenPort}/");
                    _httpListener.Start();
                }
                catch (HttpListenerException ex)
                {
                    Logger.Error($"[RestartTrigger] 监听端口{_config.ListenPort}失败: {ex.Message}（可能端口被占用）");
                    return;
                }

                // 启动请求处理线程（兼容.NET Framework）
                _ = HandleIncomingRequestsAsync(_cts.Token);
                Logger.Info($"[RestartTrigger] 插件已启用，监听端口: {_config.ListenPort}（已移除Host验证）");
            }
            catch (Exception ex)
            {
                Logger.Error($"[RestartTrigger] 插件启动失败: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public override void Disable()
        {
            try
            {
                _cts?.Cancel();
                if (_httpListener != null)
                {
                    if (_httpListener.IsListening)
                        _httpListener.Stop();
                    _httpListener.Close();
                }
                Logger.Info("[RestartTrigger] 插件已禁用");
            }
            catch (Exception ex)
            {
                Logger.Error($"[RestartTrigger] 插件卸载失败: {ex.Message}");
            }
        }

        public override void LoadConfigs()
        {
            string pluginConfigDir = Path.Combine(PathManager.Configs.FullName, "Plugins");
            string configPath = Path.Combine(pluginConfigDir, $"{Name}.yml");
            Directory.CreateDirectory(pluginConfigDir);

            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            if (!File.Exists(configPath))
            {
                _config = new Config
                {
                    ListenPort = 8080,
                    AuthKey = Guid.NewGuid().ToString("N"),
                    CooldownSeconds = 300
                };
                File.WriteAllText(configPath, serializer.Serialize(_config), Encoding.UTF8);
                Logger.Warn($"[RestartTrigger] 未找到配置文件，已生成默认配置: {configPath}");
                Logger.Warn($"[RestartTrigger] 默认鉴权Key: {_config.AuthKey}（请务必修改！）");
                return;
            }

            try
            {
                _config = deserializer.Deserialize<Config>(File.ReadAllText(configPath, Encoding.UTF8)) ?? new Config();
            }
            catch (Exception ex)
            {
                Logger.Error($"[RestartTrigger] 配置文件解析失败，使用默认配置: {ex.Message}");
                _config = new Config
                {
                    ListenPort = 8080,
                    AuthKey = Guid.NewGuid().ToString("N"),
                    CooldownSeconds = 300
                };
            }

            if (string.IsNullOrEmpty(_config.AuthKey))
            {
                _config.AuthKey = Guid.NewGuid().ToString("N");
                Logger.Warn($"[RestartTrigger] 鉴权Key为空，自动生成: {_config.AuthKey}");
                File.WriteAllText(configPath, serializer.Serialize(_config), Encoding.UTF8);
            }
        }
        #endregion

        #region HTTP请求处理（修复.NET Framework兼容问题）
        private async Task HandleIncomingRequestsAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 修复：替换WaitAsync（.NET Framework无此方法），改用Task.WhenAny实现取消逻辑
                    var getContextTask = _httpListener.GetContextAsync();
                    var completedTask = await Task.WhenAny(getContextTask, Task.Delay(int.MaxValue, cancellationToken));

                    // 检查是否是取消信号触发
                    if (completedTask == getContextTask && !cancellationToken.IsCancellationRequested)
                    {
                        HttpListenerContext context = getContextTask.Result;
                        _ = ProcessRequestAsync(context);
                    }
                    else
                    {
                        // 收到取消信号，退出循环
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        Logger.Error($"[RestartTrigger] 接收请求异常: {ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;
                string responseText;
                int statusCode = 200;

                // 只处理GET请求
                if (request.HttpMethod != "GET")
                {
                    responseText = "{\"error\":\"只支持GET请求\"}";
                    statusCode = 405;
                }
                else
                {
                    // 验证鉴权Key
                    string key = request.QueryString["key"];
                    if (string.IsNullOrEmpty(key) || key != _config.AuthKey)
                    {
                        responseText = "{\"error\":\"鉴权失败\"}";
                        statusCode = 401;
                    }
                    else
                    {
                        // 检查冷却时间
                        lock (_timeLock)
                        {
                            DateTime now = DateTime.Now;
                            TimeSpan elapsed = now - _lastTriggerTime;

                            if (elapsed.TotalSeconds < _config.CooldownSeconds)
                            {
                                int remaining = _config.CooldownSeconds - (int)elapsed.TotalSeconds;
                                responseText = $"{{\"error\":\"冷却中，请{remaining}秒后重试\"}}";
                                statusCode = 429;
                            }
                            else
                            {
                                _lastTriggerTime = now;

                                // 触发重启（异步执行）
                                _ = TriggerRestartAsync();

                                responseText = "{\"message\":\"重启命令已触发\"}";
                                statusCode = 200;
                            }
                        }
                    }
                }

                // 修复：WriteAsync需要传入offset和count（.NET Framework重载要求）
                byte[] buffer = Encoding.UTF8.GetBytes(responseText);
                response.StatusCode = statusCode;
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length); // 补充offset和count参数
                response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Logger.Error($"[RestartTrigger] 处理请求异常: {ex.Message}");
            }
        }

        private async Task TriggerRestartAsync()
        {
            try
            {
                Logger.Warn("[RestartTrigger] 收到远程重启指令，准备重启服务器...");

                // 异步延迟以确保HTTP响应先返回
                await Task.Delay(1000);

                // 调用LabAPI原生的Server.Restart方法重启服务器
                Server.Restart();
            }
            catch (Exception ex)
            {
                Logger.Error($"[RestartTrigger] 重启过程异常: {ex.Message}\n{ex.StackTrace}");
            }
        }
        #endregion

        #region 配置类
        public class Config
        {
            public int ListenPort { get; set; }
            public string AuthKey { get; set; }
            public int CooldownSeconds { get; set; }
        }
        #endregion
    }
}