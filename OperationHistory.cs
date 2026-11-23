namespace BveVehicleEditor
{
    /// <summary>
    /// ユーザーの操作を新しい順に一定数だけ覚えておく（クラッシュログに書き出すため）
    /// </summary>
    internal static class OperationHistory
    {
        // 覚えておく操作の最大数（古いものから消える）
        private const int MaxCount = 100;

        // 操作の記録（古い順）
        private static readonly Queue<string> _entries = new();

        // 複数のスレッドから同時に触られても壊れないようにする鍵
        private static readonly object _lock = new();

        /// <summary>
        /// 操作を1つ記録する
        /// </summary>
        public static void Add(string operation)
        {
            string entry = $"{DateTime.Now:HH:mm:ss} {operation}";

            lock (_lock)
            {
                _entries.Enqueue(entry);
                // 上限を超えたら一番古いものを消す
                if (_entries.Count > MaxCount)
                {
                    _entries.Dequeue();
                }
            }
        }

        /// <summary>
        /// 記録した操作を古い順にすべて返す
        /// </summary>
        public static string[] GetAll()
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }
}
