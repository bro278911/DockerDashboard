// FastDev 測試共用 %APPDATA% override 檔與 FileSystemWatcher，跨 class 並行會互撞造成 flaky；
// 全套僅數十筆、毫秒級，序列化成本可忽略。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
