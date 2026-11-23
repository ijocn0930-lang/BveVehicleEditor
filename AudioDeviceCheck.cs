using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BveVehicleEditor
{
    /// <summary>
    /// PC で音を出せるかを調べる（サウンドのタブの再生ボタンで使う）
    /// 音の出る機器の数（winmm の waveOutGetNumDevs）と、Windows のサービス「Windows Audio（Audiosrv）」「Windows Audio Endpoint Builder（AudioEndpointBuilder）」が
    /// 動いているか（advapi32 の QueryServiceStatus）を読むだけ。サービスの設定は変えない
    /// </summary>
    internal static class AudioDeviceCheck
    {
        /// <summary>
        /// 音を出せない理由
        /// </summary>
        internal enum AudioProblem
        {
            // 問題なし（音の出る機器がある）
            None,
            // Windows Audio のサービスが止まっている
            AudioServiceStopped,
            // Windows Audio Endpoint Builder のサービスが止まっている（Windows Audio も一緒に止まる）
            EndpointBuilderStopped,
            // サービスは動いているが、音の出る機器がない・無効になっている
            NoDevice,
            // 機器がないが、サービスの状態を読めず、理由を分けられない
            Unknown,
        }

        // サービスの開始・停止には管理者の権限が要る（ユーザーが気づいた）
        private static string AdminNote => Loc.T("Aud.001");

        // サービスの名前（Windows の内部の名前）
        private const string AudioServiceName = "Audiosrv";
        private const string EndpointBuilderName = "AudioEndpointBuilder";

        /// <summary>
        /// 音を出せるかを調べる。先にサービスの状態を読み（止まっていればすぐその理由）、動いていれば機器の数を数える（0 なら機器がない）
        /// 機器を先に数えると、アプリで音を鳴らしたあとにサービスを止めたとき、前の状態のまま「機器がある」と返り、MediaPlayer が失敗するまで 4 秒ほど待った（ユーザーが気づいた）
        /// </summary>
        internal static AudioProblem Check()
        {
            bool? endpointRunning = IsServiceRunning(EndpointBuilderName);
            if (endpointRunning == false) return AudioProblem.EndpointBuilderStopped;
            bool? audioRunning = IsServiceRunning(AudioServiceName);
            if (audioRunning == false) return AudioProblem.AudioServiceStopped;

            int devices = CountOutputDevices();
            if (devices != 0) return AudioProblem.None;
            return endpointRunning == true && audioRunning == true ? AudioProblem.NoDevice : AudioProblem.Unknown;
        }

        /// <summary>
        /// 理由の日本語の文（None なら空）
        /// </summary>
        internal static string Describe(AudioProblem problem)
        {
            return problem switch
            {
                AudioProblem.EndpointBuilderStopped => Loc.T("Aud.002") + AdminNote,
                AudioProblem.AudioServiceStopped => Loc.T("Aud.003") + AdminNote,
                AudioProblem.NoDevice => Loc.T("Aud.004"),
                AudioProblem.Unknown => Loc.T("Aud.005"),
                _ => string.Empty,
            };
        }

        // 音の出る機器の数（すぐ返る。サービスが止まっていると 0）。調べられないときは -1（「ある」として鳴らしてみる）
        private static int CountOutputDevices()
        {
            try
            {
                return (int)waveOutGetNumDevs();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return -1;
            }
        }

        // サービスが動いているか（読めなければ null）。読むだけの権限（SC_MANAGER_CONNECT・SERVICE_QUERY_STATUS）で開く
        private static bool? IsServiceRunning(string serviceName)
        {
            IntPtr manager = IntPtr.Zero;
            IntPtr service = IntPtr.Zero;
            try
            {
                manager = OpenSCManager(null, null, ScManagerConnect);
                if (manager == IntPtr.Zero) return null;
                service = OpenService(manager, serviceName, ServiceQueryStatus);
                if (service == IntPtr.Zero) return null;
                if (!QueryServiceStatus(service, out ServiceStatus status)) return null;
                return status.CurrentState == ServiceRunning;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
            {
                return null;
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                if (manager != IntPtr.Zero) CloseServiceHandle(manager);
            }
        }

        private const uint ScManagerConnect = 0x0001;
        private const uint ServiceQueryStatus = 0x0004;
        private const uint ServiceRunning = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType;
            public uint CurrentState;
            public uint ControlsAccepted;
            public uint Win32ExitCode;
            public uint ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

        [DllImport("winmm.dll")]
        private static extern uint waveOutGetNumDevs();

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr handle);
    }
}
