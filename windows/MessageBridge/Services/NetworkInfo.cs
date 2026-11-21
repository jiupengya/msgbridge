using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MessageBridge.Services;

/// <summary>
/// 本机网络状态判断。
///
/// 「蓝牙兜底」只在电脑插着**有线以太网**时才启用：这时候手机能不能连上
/// 全看它的 WiFi，出问题才需要我们从电脑这边拉一把。电脑自己走 WiFi 时
/// 两台机器在同一张网里，链路本来就是通的，不做额外动作。
/// </summary>
public static class NetworkInfo
{
    /// <summary>虚拟网卡的关键字，这些虽然也报成 Ethernet，但不是真的插网线。</summary>
    private static readonly string[] VirtualHints =
    {
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox",
        "tap", "tun", "vpn", "loopback", "bluetooth", "wsl", "docker",
    };

    /// <summary>当前是否有一块真的插着网线的以太网卡在工作。</summary>
    public static bool IsOnEthernet()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;

                var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (VirtualHints.Any(h => text.Contains(h))) continue;

                var hasIpv4 = ni.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (!hasIpv4) continue;

                return true;
            }
        }
        catch
        {
            // 查询失败时按「不是以太网」处理，宁可不启用也不乱发指令
        }
        return false;
    }

    /// <summary>给界面用的一句话描述，例如「以太网 192.168.66.244」。</summary>
    public static string DescribeEthernet()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;

                var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (VirtualHints.Any(h => text.Contains(h))) continue;

                var ip = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;
                if (ip == null) continue;

                return $"{ni.Name} {ip}";
            }
        }
        catch
        {
            // 忽略
        }
        return string.Empty;
    }
}
