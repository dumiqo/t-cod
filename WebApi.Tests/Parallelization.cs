// решение: явное число потоков — по умолчанию MSTest берёт число ядер, и на многоядерной машине хосты тестов
// со своими пулами упрутся в max_connections; 8 хостов параллельно проверено в замерах задачи 5.
[assembly: Parallelize(Workers = 8, Scope = ExecutionScope.MethodLevel)]
