﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NewLife.Log;
using NewLife.Model;
#if __WIN__
using System.Management;
using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;
#endif

namespace NewLife
{
    /// <summary>机器信息</summary>
    /// <remarks>
    /// 刷新信息成本较高，建议采用单例模式
    /// </remarks>
    public class MachineInfo
    {
        #region 属性
        /// <summary>系统名称</summary>
        public String OSName { get; set; }

        /// <summary>系统版本</summary>
        public String OSVersion { get; set; }

        /// <summary>处理器序列号</summary>
        public String Processor { get; set; }

        /// <summary>处理器序列号</summary>
        public String CpuID { get; set; }

        /// <summary>唯一标识</summary>
        public String UUID { get; set; }

        /// <summary>机器标识</summary>
        public String Guid { get; set; }

        /// <summary>内存总量</summary>
        public UInt64 Memory { get; set; }

#if __WIN__
        private ComputerInfo _cinfo;
        /// <summary>可用内存</summary>
        public UInt64 AvailableMemory => _cinfo.AvailablePhysicalMemory;

        private PerformanceCounter _cpuCounter;
        /// <summary>CPU占用率</summary>
        public Single CpuRate => _cpuCounter == null ? 0 : (_cpuCounter.NextValue() / 100);
#else
        /// <summary>可用内存</summary>
        public UInt64 AvailableMemory { get; private set; }

        /// <summary>CPU占用率</summary>
        public Single CpuRate { get; private set; }
#endif

        /// <summary>温度</summary>
        public Double Temperature { get; set; }
        #endregion

        #region 构造
        /// <summary>实例化机器信息</summary>
        public MachineInfo() { }

        /// <summary>当前机器信息</summary>
        public static MachineInfo Current { get; set; }

        /// <summary>异步注册一个初始化后的机器信息实例</summary>
        /// <returns></returns>
        public static Task<MachineInfo> RegisterAsync()
        {
            return Task.Factory.StartNew(() =>
            {
                var mi = new MachineInfo();

                mi.Init();

                Current = mi;

                // 注册到对象容器
                ObjectContainer.Current.Register<MachineInfo>(mi);

                return mi;
            });
        }

        /// <summary>从对象容器中获取一个已注册机器信息实例</summary>
        /// <returns></returns>
        public static MachineInfo Resolve() => ObjectContainer.Current.ResolveInstance<MachineInfo>();
        #endregion

        #region 方法
        /// <summary>刷新</summary>
        public void Init()
        {
#if __CORE__
            var osv = Environment.OSVersion;
            OSVersion = osv.Version + "";
            OSName = (osv + "").TrimStart("Microsoft").TrimEnd(OSVersion).Trim();

            // 特别识别Linux发行版
            if (Runtime.Linux)
            {
                OSName = GetLinuxName();

                // 树莓派优先 Model
                if (TryGet("/proc/cpuinfo", new[] { "Model" }, out var str)) Processor = str;
                if (Processor.IsNullOrEmpty() && TryGet("/proc/cpuinfo", new[] { "cpu model", "model name", "Hardware" }, out str)) Processor = str;
                if (TryGet("/proc/cpuinfo", new[] { "Serial", "serial" }, out str)) CpuID = str;
                if (TryGet("/proc/meminfo", new[] { "MemTotal" }, out str)) Memory = (UInt64)str.TrimEnd(" kB").ToInt() * 1024;
                if (TryGet("/proc/meminfo", new[] { "MemAvailable" }, out str)) AvailableMemory = (UInt64)str.TrimEnd(" kB").ToInt() * 1024;
                if (TryGet("/sys/class/thermal/thermal_zone0/temp", null, out str)) Temperature = str.ToDouble() / 1000;

                var dmi = Execute("dmidecode");
                if (!dmi.IsNullOrEmpty())
                {
                    if (TryFind(dmi, new[] { "ID" }, out str)) CpuID = str.Replace(" ", null);
                    if (TryFind(dmi, new[] { "UUID" }, out str)) UUID = str;
                    //if (TryFind(dmi, new[] { "Serial Number" }, out str)) Guid = str;
                }
            }
#else
            // 性能计数器的初始化非常耗时
            Task.Factory.StartNew(() =>
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total")
                {
                    MachineName = "."
                };
                _cpuCounter.NextValue();
            });

            var reg = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (reg != null) Guid = reg.GetValue("MachineGuid") + "";
            if (Guid.IsNullOrEmpty())
            {
                reg = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                if (reg != null) Guid = reg.GetValue("MachineGuid") + "";
            }

            var ci = new ComputerInfo();
            OSName = ci.OSFullName.TrimStart("Microsoft").Trim();
            OSVersion = ci.OSVersion;
            Memory = ci.TotalPhysicalMemory;

            _cinfo = ci;

            Processor = GetInfo("Win32_Processor", "Name");
            CpuID = GetInfo("Win32_Processor", "ProcessorId");
            UUID = GetInfo("Win32_ComputerSystemProduct", "UUID");

            // 读取主板温度，不太准。标准方案是ring0通过IOPort读取CPU温度，太难在基础类库实现
            var str = GetInfo("MSAcpi_ThermalZoneTemperature", "CurrentTemperature");
            if (!str.IsNullOrEmpty()) Temperature = (str.ToDouble() - 2732) / 10.0;
#endif
        }
        #endregion

        #region 辅助
        /// <summary>获取Linux发行版名称</summary>
        /// <returns></returns>
        public static String GetLinuxName()
        {
            var fr = "/etc/redhat-release";
            var dr = "/etc/debian-release";
            if (File.Exists(fr))
                return File.ReadAllText(fr).Trim();
            else if (File.Exists(dr))
                return File.ReadAllText(dr).Trim();
            else
            {
                var sr = "/etc/os-release";
                if (File.Exists(sr)) return File.ReadAllText(sr).SplitAsDictionary("=", "\n", true)["PRETTY_NAME"].Trim();
            }

            return null;
        }

        private static Boolean TryGet(String file, String[] keys, out String value)
        {
            value = null;

            if (!File.Exists(file)) return false;

            using var reader = new StreamReader(file);
            return Find(reader, keys, out value);
        }

        private static Boolean TryFind(String txt, String[] keys, out String value)
        {
            using var reader = new StringReader(txt);
            return Find(reader, keys, out value);
        }

        private static Boolean Find(TextReader reader, String[] keys, out String value)
        {
            value = null;

            //while (!reader.EndOfStream)
            while (true)
            {
                // 按行读取
                var line = reader.ReadLine();
                if (line == null) break;
                if (line != null)
                {
                    if (keys == null || keys.Length == 0)
                    {
                        value = line.Trim();
                        return true;
                    }

                    // 分割
                    var p = line.IndexOf(':');
                    //if (p > 0 && line.Substring(0, p).Trim().EqualIgnoreCase(keys))
                    if (p > 0 && keys.Contains(line.Substring(0, p).Trim()))
                    {
                        value = line.Substring(p + 1).Trim();
                        return true;
                    }
                }
            }

            return false;
        }

        private static String Execute(String cmd, String arguments = null)
        {
            var psi = new ProcessStartInfo(cmd, arguments) { RedirectStandardOutput = true };
            var process = Process.Start(psi);
            if (!process.WaitForExit(3_000)) return null;

            return process.StandardOutput.ReadToEnd();
        }
        #endregion

        #region WMI辅助
#if __WIN__
        /// <summary>获取WMI信息</summary>
        /// <param name="path"></param>
        /// <param name="property"></param>
        /// <returns></returns>
        public static String GetInfo(String path, String property)
        {
            // Linux Mono不支持WMI
            if (Runtime.Mono) return "";

            var bbs = new List<String>();
            try
            {
                var wql = String.Format("Select {0} From {1}", property, path);
                var cimobject = new ManagementObjectSearcher(wql);
                var moc = cimobject.Get();
                foreach (var mo in moc)
                {
                    if (mo != null &&
                        mo.Properties != null &&
                        mo.Properties[property] != null &&
                        mo.Properties[property].Value != null)
                        bbs.Add(mo.Properties[property].Value.ToString());
                }
            }
            catch (Exception ex)
            {
                //XTrace.WriteException(ex);
                XTrace.WriteLine("WMI.GetInfo({0})失败！{1}", path, ex.Message);
                return "";
            }

            bbs.Sort();

            return bbs.Distinct().Join();
        }
#endif
        #endregion
    }
}