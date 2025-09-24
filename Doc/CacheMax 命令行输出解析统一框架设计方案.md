
 一、现状分析

 1. Robocopy调用点 (4处)

     - CacheManagerService.ExecuteRobocopyAsync: 初始复制，使用/MT多线程
     - CacheManagerService.ExecuteRobocopySync: 同步模式，用于强制同步
     - MainWindow.CheckSingleItemIntegrityAsync: 使用/L参数检查差异
     - MainWindow.SyncSingleItemAsync: 执行实际同步，使用/PURGE参数

 2. FastCopy调用点 (4处)

     - FastCopyService.CopyWithVerifyDetailedAsync: 带校验的文件复制
     - FastCopyService.SyncChangesAsync: 同步变更文件
     - FastCopyService.CopyDirectoryAsync: 目录复制
     - FastCopyService.ExecuteWithMonitoringAsync: 底层执行方法

 二、当前问题

 1. 输出解析不一致

     - Robocopy: 多种解析方式（正则匹配数字、字符串包含检查）
     - FastCopy: 简单依赖ExitCode判断
     - 缺乏标准化的输出模型

 2. 错误处理分散

     - Process == null检查重复
     - 超时处理只在FastCopy中实现
     - 错误信息提取不统一

 3. 边缘案例处理不全

     - Robocopy退出码9的特殊情况
     - 进程无法启动
     - 输出编码问题（中文乱码）
     - 进程挂起/超时
     - 输出缓冲区溢出

 三、统一框架设计

1. 创建统一的命令执行器基类

     public abstract class CommandLineExecutor
     {
         // 统一的执行结果模型
         public class ExecutionResult
         {
             public bool Success { get; set; }
             public int ExitCode { get; set; }
             public List<string> StandardOutput { get; set; }
             public List<string> ErrorOutput { get; set; }
             public Dictionary<string, object> ParsedData { get; set; }
             public string ErrorMessage { get; set; }
             public TimeSpan ExecutionTime { get; set; }
         }

         // 统一的执行方法
         protected abstract Task<ExecutionResult> ExecuteAsync(
             string command, 
             string arguments,
             IProgress<string> progress,
             CancellationToken cancellationToken,
             int timeoutSeconds = 300);

         // 统一的输出解析接口
         protected abstract bool ParseOutput(
             List<string> outputLines, 
             out Dictionary<string, object> parsedData);

         // 统一的成功判断逻辑
         protected abstract bool DetermineSuccess(
             int exitCode, 
             Dictionary<string, object> parsedData,
             List<string> errorOutput);
     }

     2. Robocopy专用解析器

     public class RobocopyExecutor : CommandLineExecutor
     {
         public class RobocopyStatistics
         {
             public long TotalDirs { get; set; }
             public long CopiedDirs { get; set; }
             public long TotalFiles { get; set; }
             public long CopiedFiles { get; set; }
             public long TotalBytes { get; set; }
             public long CopiedBytes { get; set; }
             public int FailedDirs { get; set; }
             public int FailedFiles { get; set; }
         }

         protected override bool ParseOutput(...)
         {
             // 统一的表格解析逻辑
             // 支持中英文输出格式
             // 提取目录、文件、字节三行统计
         }

         protected override bool DetermineSuccess(...)
         {
             // 智能成功判断
             // 1. ExitCode < 8 = 官方成功
             // 2. ExitCode 8-15 + 所有文件复制成功 = 实质成功
             // 3. ExitCode >= 16 = 严重失败
         }
     }

     3. FastCopy专用解析器

     public class FastCopyExecutor : CommandLineExecutor
     {
         protected override bool ParseOutput(...)
         {
             // 解析FastCopy特有的输出格式
             // 提取传输速度、完成百分比等
         }

         protected override bool DetermineSuccess(...)
         {
             // FastCopy成功判断
             // ExitCode == 0 = 成功
             // 检查错误输出中的关键字
         }
     }

     4. 通用错误处理器

     public class ProcessErrorHandler
     {
         public enum ErrorType
         {
             ProcessStartFailed,     // 进程启动失败
             Timeout,               // 超时
             AccessDenied,          // 权限不足
             PathNotFound,          // 路径不存在
             DiskFull,              // 磁盘空间不足
             NetworkError,          // 网络错误
             UnknownError           // 未知错误
         }

         public static ErrorType IdentifyError(
             int exitCode, 
             List<string> errorOutput,
             Exception exception = null)
         {
             // 统一的错误识别逻辑
         }

         public static string GetUserFriendlyMessage(ErrorType errorType)
         {
             // 返回用户友好的错误信息
         }
     }

     5. 进程监控管理器

     public class ProcessMonitor
     {
         // 统一的超时处理
         // 进程状态监控
         // 资源使用监控
         // 自动清理僵尸进程
     }

     四、实施步骤

     第1步：创建基础框架

     6. 创建CommandLineExecutor基类
     7. 创建ExecutionResult统一结果模型
     8. 创建ProcessErrorHandler错误处理器
     9. 创建ProcessMonitor进程监控器

     第2步：实现专用解析器

     10. 实现RobocopyExecutor
       - 移植现有的IsRobocopyCompletelySuccessful逻辑
       - 增强表格解析，支持英文输出
       - 实现退出码智能判断
     2. 实现FastCopyExecutor
       - 增强输出解析
       - 添加进度提取

     第3步：重构现有代码

     1. 替换CacheManagerService中的Robocopy调用
     2. 替换MainWindow中的Robocopy调用
     3. 替换FastCopyService中的执行逻辑

     第4步：增强功能

     4. 添加输出日志持久化
     5. 添加执行历史记录
     6. 添加性能统计
     7. 添加重试机制

     五、预期收益

     8. 一致性: 所有命令行工具使用统一接口
     9. 可维护性: 集中管理输出解析逻辑
     10. 可靠性: 统一的错误处理和超时机制
     11. 可扩展性: 易于添加新的命令行工具
     12. 可测试性: 解析逻辑独立，易于单元测试
     13. 用户体验: 统一、清晰的错误提示