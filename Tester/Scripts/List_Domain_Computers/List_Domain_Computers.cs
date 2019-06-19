using System;
using System.Collections.Generic;
using System.Configuration.Install;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

public class List_Domain_Computers
{

	public static int ProcessArguments(string[] args)
	{
		//for (int i = 0; i < args.Length; i++)
		//	Console.WriteLine(string.Format("{0}. {1}", i, args[i]));
		// Requires System.Configuration.Installl reference.
		var ic = new InstallContext(null, args);
		var domain = ic.Parameters["domain"];
		if (string.IsNullOrEmpty(domain))
			domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
		Console.WriteLine("Domain: {0}", domain);
		Console.WriteLine("Folder: {0}", Environment.CurrentDirectory);
		var list = GetComputers(domain);
		Console.WriteLine("{0} names exported", list.Count);
		// Apply types
		list.Where(x => x.Os.Contains("Windows")).ToList().ForEach(x => x.Type = "Client");
		list.Where(x => x.Os.Contains("Server")).ToList().ForEach(x => x.Type = "Server");
		list = list.OrderByDescending(x => x.Type).ThenBy(x => x.Os).ThenBy(x => x.Name).ToList();
		Write(list, domain + "_computers");
		Console.WriteLine();
		return 0;
	}

	static void Write(List<Computer> list, string file, bool? active = null)
	{
		var sb = new StringBuilder();
		var now = DateTime.Now;
		var suffix = "";
		if (active.HasValue)
			suffix = active.Value ? "_active" : "_passive";
		var fileName = file + suffix + ".xls";
		Console.WriteLine("{0}: Test", fileName);
		UpdateIsOnline(list);
		if (active.HasValue)
		{
			var activeList = list.Where(x => !string.IsNullOrEmpty(x.OpenPort)).ToList();
			var absentList = list.Except(activeList).ToList();
			list = active.Value ? activeList : absentList;
		}
		Console.WriteLine("{0}: Write", fileName);
		var table = new Table();
		table.Rows = list;
		Serialize(table, fileName);
	}

	public static List<Computer> GetComputers(string domain)
	{
		var list = new List<Computer>();
		var entry = new DirectoryEntry("LDAP://" + domain);
		var ds = new DirectorySearcher(entry);
		//ds.PropertiesToLoad.AddRange(new string[] { "samAccountName", "lastLogon" });
		ds.Filter = ("(objectClass=computer)");
		ds.SizeLimit = int.MaxValue;
		ds.PageSize = int.MaxValue;
		var all = ds.FindAll().Cast<SearchResult>().ToArray();
		Console.Write("Progress: ");
		for (int i = 0; i < all.Length; i++)
		{
			var result = all[i];
			var sr = result.GetDirectoryEntry();
			var name = sr.Name;
			var os = string.Format("{0}", sr.Properties["OperatingSystem"].Value)
				.Replace("Standard", "")
				.Replace("Datacenter", "")
				.Replace("Enterprise", "")
				.Replace("Professional", "")
				.Replace("Business", "")
				.Replace("PC Edition", "")
				.Replace("Pro", "")
				.Replace("�", "")
				.Replace("�", "")
				.Trim();
			var sp = os.Contains("Windows")
				? string.Format("{0}", sr.Properties["OperatingSystemServicePack"].Value)
				.Replace("Service Pack ", "SP")
				.Trim()
				: "";
			var ov = string.Format("{0}", sr.Properties["OperatingSystemVersion"].Value).Trim();
			DateTime? ll = null;
			//if (sr.Properties["LastLogonTimeStamp"] != null && sr.Properties["LastLogonTimeStamp"].Count > 0)
			//{
			//	long lastLogon = (long)sr.Properties["LastLogonTimeStamp"][0];
			//	ll = DateTime.FromFileTime(lastLogon);
			//}
			if (name.StartsWith("CN="))
				name = name.Remove(0, "CN=".Length);
			string ips = "";
			var host = string.Format("{0}.{1}", name, domain);
			try
			{
				var ipaddress = Dns.GetHostAddresses(host);
				var addresses = ipaddress.Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).FirstOrDefault();
				ips = string.Join(" ", addresses);
			}
			catch (Exception)
			{
				ips = "Unknown";
				//throw;
				continue;
			}
			var computer = new Computer()
			{
				Name = name,
				Os = os,
				OsVersion = ov,
				OsPack = sp,
				Address = ips,
				LastLogon = ll,
				OpenPort = null,
			};
			list.Add(computer);
			Write(i, all.Length);
		}
		using (var context = new PrincipalContext(ContextType.Domain, domain))
		{
			using (var searcher = new PrincipalSearcher(new ComputerPrincipal(context)))
			{
				foreach (var result in searcher.FindAll())
				{
					var auth = result as AuthenticablePrincipal;
					if (auth != null)
					{
						var computer = list.FirstOrDefault(x => x.Name == auth.Name);
						if (computer == null)
							continue;
						if (auth.LastLogon.HasValue)
						{
							var dateTime = auth.LastLogon.Value;
							dateTime = new DateTime(
								dateTime.Ticks - (dateTime.Ticks % TimeSpan.TicksPerMinute),
								dateTime.Kind
							);
							computer.LastLogon = dateTime;
						}
						computer.SamAccountName = auth.SamAccountName;
						computer.UserPrincipalName = auth.UserPrincipalName;

					}
				}
			}
		}
		Console.WriteLine();
		ds.Dispose();
		entry.Dispose();
		return list;
	}

	public static void Write(int i, int max)
	{
		var l = max.ToString().Length;
		var s = string.Format("{0," + l + "}/{1}", i + 1, max);
		Console.CursorVisible = i + 1 == max;
		if (i > 0)
			for (var c = 0; c < s.Length; c++)
				Console.Write("\b");
		Console.Write(s);
	}

	#region Ping

	public static bool Ping(string hostNameOrAddress, int timeout = 1000)
	{
		Exception error;
		return Ping(hostNameOrAddress, timeout, out error);
	}

	public static bool Ping(string hostNameOrAddress, int timeout, out Exception error)
	{
		var success = false;
		error = null;
		var sw = new System.Diagnostics.Stopwatch();
		sw.Start();
		System.Net.NetworkInformation.PingReply reply = null;
		Exception replyError = null;
		// Use proper threading, because other asynchronous classes
		// like "Tasks" have problems with Ping.
		var ts = new System.Threading.ThreadStart(delegate ()
		{
			var ping = new System.Net.NetworkInformation.Ping();
			try
			{
				reply = ping.Send(hostNameOrAddress);
			}
			catch (Exception ex)
			{
				replyError = ex;
			}
			ping.Dispose();
		});
		var t = new System.Threading.Thread(ts);
		t.Start();
		t.Join(timeout);
		if (reply != null)
		{
			success = (reply.Status == System.Net.NetworkInformation.IPStatus.Success);
		}
		else if (replyError != null)
		{
			error = replyError;
		}
		else
		{
			error = new Exception("Ping timed out (" + timeout.ToString() + "): " + sw.Elapsed.ToString());
		}
		return success;
	}

	#endregion

	#region Helper Methods


	public static bool IsPortOpen(string host, int port, int timeout = 2000, int retry = 1)
	{
		var retryCount = 0;
		while (retryCount < retry)
		{
			// Logical delay without blocking the current thread.
			if (retryCount > 0)
				System.Threading.Tasks.Task.Delay(timeout).Wait();
			var client = new System.Net.Sockets.TcpClient();
			try
			{
				var result = client.BeginConnect(host, port, null, null);
				var success = result.AsyncWaitHandle.WaitOne(timeout);
				if (success)
				{
					client.EndConnect(result);
					return true;
				}
			}
			catch
			{
				// ignored
			}
			finally
			{
				client.Close();
				retryCount++;
			}
		}
		return false;
	}

	// The following byte stream contains the necessary message
	// to request a NetBios name from a machine
	static byte[] NameRequest = new byte[]{
			0x80, 0x94, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00, 0x20, 0x43, 0x4b, 0x41,
			0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
			0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
			0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
			0x41, 0x41, 0x41, 0x41, 0x41, 0x00, 0x00, 0x21,
			0x00, 0x01 };


	/// <summary>
	/// Request NetBios name on UDP port 137. 
	/// </summary>
	/// <returns></returns>
	static bool CheckNetBios(Computer computer)
	{
		var receiveBuffer = new byte[1024];
		var requestSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
		requestSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 2000);
		var addressList = Dns.GetHostAddresses(computer.Name);
		if (addressList.Length == 0)
		{
			//Console.WriteLine("NetBIOS: {0} host could not be found.", computer.Name);
			return false;
		}
		EndPoint remoteEndpoint = new IPEndPoint(addressList[0], 137);
		var originEndpoint = new IPEndPoint(IPAddress.Any, 0);
		requestSocket.Bind(originEndpoint);
		requestSocket.SendTo(NameRequest, remoteEndpoint);
		try
		{
			var receivedByteCount = requestSocket.ReceiveFrom(receiveBuffer, ref remoteEndpoint);
			if (receivedByteCount >= 90)
			{
				var enc = new ASCIIEncoding();
				var deviceName = enc.GetString(receiveBuffer, 57, 16).Trim();
				var networkName = enc.GetString(receiveBuffer, 75, 16).Trim();
				return true;
				//Console.WriteLine("NetBIOS: {0} is online.", deviceName);
			}
		}
		catch (SocketException)
		{
			//Console.WriteLine("NetBIOS: {0} could not be identified.", computer.Name);
		}
		return false;
	}

	static int UpdateIsOnlineCount;
	static int UpdateIsOnlineTotal;

	public static void UpdateIsOnline(List<Computer> computers)
	{
		UpdateIsOnlineCount = 0;
		UpdateIsOnlineTotal = computers.Count;
		Parallel.ForEach(computers,
		new ParallelOptions { MaxDegreeOfParallelism = 16 },
		   x => UpdateIsOnline(x)
		);
	}

	static void UpdateIsOnline(Computer computer)
	{
		try
		{
			// NetBIOS UDP 137.
			if (CheckNetBios(computer))
				computer.OpenPort = "UDP/137";
			// RPC TCP 135.
			if (string.IsNullOrEmpty(computer.OpenPort) && IsPortOpen(computer.Name, 135))
				computer.OpenPort = "TCP/135";
			// RDP TCP 3389.
			if (string.IsNullOrEmpty(computer.OpenPort) && IsPortOpen(computer.Name, 3389))
				computer.OpenPort = "TCP/3389";
			// Try to PING.
			if (string.IsNullOrEmpty(computer.OpenPort) && Ping(computer.Name, 2000))
				computer.OpenPort = "ICMP";
			// Report.
			System.Threading.Interlocked.Increment(ref UpdateIsOnlineCount);
			var percent = (decimal)UpdateIsOnlineCount / (decimal)UpdateIsOnlineTotal * 100m;
			Console.WriteLine("{0," + UpdateIsOnlineTotal.ToString().Length + "}. {1,-16} Port: {2,4} - {3,5:0.0}%",
				UpdateIsOnlineCount, computer.Name, computer.OpenPort, percent);
		}
		catch (Exception ex)
		{
			Console.WriteLine("{0} Exception: {1}", computer.Name, ex.Message);
		}
	}

	#endregion

	#region Serialize

	[XmlRoot("table")]
	public class Table
	{
		[XmlElement("row")]
		public List<Computer> Rows { get; set; }
	}

	public class Computer
	{
		public string Type { get; set; }
		public string Name { get; set; }
		public string Address { get; set; }
		public string Os { get; set; }
		public string OsVersion { get; set; }
		public string OsPack { get; set; }
		[XmlIgnore] public string SamAccountName { get; set; }
		[XmlIgnore] public string UserPrincipalName { get; set; }
		public DateTime? LastLogon { get; set; }
		public string OpenPort { get; set; }
	}

	static void Serialize<T>(T o, string path)
	{
		var settings = new XmlWriterSettings();
		//settings.OmitXmlDeclaration = true;
		settings.Encoding = System.Text.Encoding.UTF8;
		settings.Indent = true;
		settings.IndentChars = "\t";
		var serializer = new XmlSerializer(typeof(T));
		// Serialize in memory first, so file will be locked for shorter times.
		var ms = new MemoryStream();
		var xw = XmlWriter.Create(ms, settings);
		serializer.Serialize(xw, o);
		File.WriteAllBytes(path, ms.ToArray());
	}

	#endregion

}

