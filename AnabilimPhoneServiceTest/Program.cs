using AnabilimPhoneServiceTest.DataService;
using AsterNET.Manager;
using AsterNET.Manager.Action;
using AsterNET.Manager.Response;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace AnabilimPhoneServiceTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            try
            {
                var binding = new BasicHttpsBinding(BasicHttpsSecurityMode.Transport);
                binding.MaxReceivedMessageSize = 1000000;
                var endpointAddress = new EndpointAddress("https://okul.k12net.com/K12NETDataService/K12NETLoginService.svc");
                var dataendpointAddress = new EndpointAddress("https://okul.k12net.com/K12NETDataService/K12NETDataService.svc");
                var client = new LoginService.K12NETLoginServiceClient(binding, endpointAddress);
                var k12User = ConfigurationManager.AppSettings["K12User"];
                var k12Password = ConfigurationManager.AppSettings["K12Password"];
                var authCookie = client.Login(k12User, k12Password);
                var logMessage = "Login Servisine Bağlandı";
                Console.WriteLine(logMessage);
                Logyaz(logMessage);
                var dataClient = new DataService.K12NETServiceClient(binding, dataendpointAddress);
                var httpRequest = new HttpRequestMessageProperty();
                httpRequest.Headers.Add(HttpRequestHeader.Cookie, authCookie);
                var connstr = ConfigurationManager.ConnectionStrings["MySQL"].ConnectionString;
                var pbxIp = ConfigurationManager.AppSettings["PbxIp"];
                var pbxPort = ConfigurationManager.AppSettings["PbxPort"];
                var pbxUser = ConfigurationManager.AppSettings["PbxUser"];
                var pbxPassword = ConfigurationManager.AppSettings["PbxPassword"];
                var contactYear = ConfigurationManager.AppSettings["ContactYear"];
                var staffYear = ConfigurationManager.AppSettings["StaffYear"];
                using (MySqlConnection conn = new MySqlConnection(connstr))
                {
                    conn.Open();
                    EnsureStudentContactSchema(conn);
                    var studentNamesByPhone = new Dictionary<string, HashSet<string>>();
                    var speedDials = new Dictionary<string, string>();
                    var syncToken = Guid.NewGuid().ToString();
                    var studentContactCount = 0;
                    logMessage = "Veritabanına Bağlandı";
                    Console.WriteLine(logMessage);
                    Logyaz(logMessage);
                    using (new OperationContextScope(dataClient.InnerChannel))
                    {
                        OperationContext.Current.OutgoingMessageProperties.Add(HttpRequestMessageProperty.Name, httpRequest);
                        var schools = dataClient.GetAuthorizedSchools();
                        logMessage = $"Toplam Okul {schools.Count()}";
                        Console.WriteLine(logMessage);
                        Logyaz(logMessage);
                        foreach (var item in schools)
                        {
                            logMessage = $"Okul : {item.Name}";
                            Console.WriteLine(logMessage);
                            Logyaz(logMessage);

                            var allStudents = new List<StudentInfo>();
                            for (int i = 0; i < 10000; i++)
                            {
                                var students = dataClient.GetStudents(item.ID, int.Parse(contactYear), i, null, null);
                                logMessage = $" Sayfa Sayısı : {i + 1} Öğrenci Kayıt Sayısı :{students.Count()}";
                                Console.WriteLine(logMessage);
                                Logyaz(logMessage);

                                if (students.Count() == 0)
                                    break;

                                allStudents.AddRange(students);
                            }

                            var contactIds = allStudents
                                .Where(x => x.ContactIDs != null)
                                .SelectMany(x => x.ContactIDs)
                                .Where(x => x != Guid.Empty)
                                .Distinct()
                                .ToArray();

                            var contactsById = new Dictionary<Guid, ContactInfo>();
                            const int contactBatchSize = 100;
                            for (var offset = 0; offset < contactIds.Length; offset += contactBatchSize)
                            {
                                var contactIdBatch = contactIds
                                    .Skip(offset)
                                    .Take(contactBatchSize)
                                    .ToArray();
                                var contacts = dataClient.GetContactsByID(contactIdBatch) ??
                                               new ContactInfo[0];

                                foreach (var contact in contacts.Where(x => x.ContactID != Guid.Empty))
                                {
                                    // Aynı veli birden fazla öğrenciye bağlı olduğunda servis aynı
                                    // ContactID'yi tekrarlayabilir. Kişi bilgisi bir kez yeterlidir.
                                    if (!contactsById.ContainsKey(contact.ContactID))
                                        contactsById.Add(contact.ContactID, contact);
                                }
                            }

                            if (contactIds.Length > 0 && contactsById.Count == 0)
                            {
                                throw new InvalidOperationException(
                                    $"{item.Name}: Veli servisi hiç kayıt döndürmedi. Eski kayıtlar korunuyor.");
                            }

                            logMessage =
                                $"{item.Name}: {allStudents.Count} öğrenci, {contactsById.Count} veli alındı.";
                            Console.WriteLine(logMessage);
                            Logyaz(logMessage);

                            foreach (var student in allStudents.Where(x => x.StudentID != Guid.Empty))
                            {
                                var studentName = SafeName(student.FirstName);
                                var studentLastName = SafeName(student.LastName);
                                var fullStudentName = (studentName + " " + studentLastName).Trim();
                                var studentContactIds = (student.ContactIDs ?? new Guid[0])
                                    .Where(x => x != Guid.Empty)
                                    .Distinct();

                                foreach (var contactId in studentContactIds)
                                {
                                    ContactInfo contactItem;
                                    if (!contactsById.TryGetValue(contactId, out contactItem))
                                    {
                                        Logyaz($"{fullStudentName}: {contactId} veli bilgisi bulunamadı.");
                                        continue;
                                    }

                                    var type = new Guid("{4f7f3ea6-9f47-dd11-87fa-0019d1638957}");
                                    var telNo = contactItem.PhoneNumbers == null
                                        ? null
                                        : contactItem.PhoneNumbers.FirstOrDefault(x => x.TypeID == type);
                                    var code = telNo != null ? telNo.Code : null;
                                    var cepNo = telNo != null ? telNo.Number.NormalizePhoneNumber() : null;

                                    if (string.IsNullOrWhiteSpace(cepNo))
                                        continue;

                                    var parentName = (SafeName(contactItem.FirstName) + " " +
                                                      SafeName(contactItem.LastName)).Trim();

                                    UpsertStudentContact(
                                        conn,
                                        studentName,
                                        studentLastName,
                                        cepNo,
                                        code,
                                        student.StudentID,
                                        contactItem.ContactID,
                                        parentName,
                                        syncToken);

                                    studentContactCount++;
                                    AddStudentPhoneAlias(studentNamesByPhone, cepNo, fullStudentName);
                                    if (!string.IsNullOrWhiteSpace(code))
                                        speedDials[code] = cepNo;

                                    logMessage = $"{fullStudentName} -> Veli: {parentName}, {cepNo}";
                                    Console.WriteLine(logMessage);
                                    Logyaz(logMessage);
                                }
                            }
                            for (int i = 0; i < 1000; i++)
                            {
                                var staff = dataClient.GetStaffs(item.ID, int.Parse(staffYear), i);
                                logMessage = $" Sayfa Sayısı : {i + 1} Staff Kayıt Sayısı :{staff.Count()}";
                                Console.WriteLine(logMessage);
                                Logyaz(logMessage);
                                foreach (var staffItem in staff)
                                {
                                    var name = staffItem.FirstName.RemoveTurish();
                                    var lastName = staffItem.LastName.RemoveTurish();
                                    var type = new Guid("{4f7f3ea6-9f47-dd11-87fa-0019d1638957}");
                                    var TelNo = staffItem.PhoneNumbers.FirstOrDefault(x => x.TypeID == type);
                                    var code = TelNo != null ? TelNo.Code : null;
                                    var cepNo = TelNo != null ? TelNo.Number.NormalizePhoneNumber() : null;
                                    var found = hasData(code, cepNo, conn);
                                    var mysqlCommandLog = new MySqlCommand("INSERT INTO RehberLog (adi,soyadi,Telefon1,kayittipi,KISAKOD,Firma) VALUE (@adi,@soyadi,@Telefon1,@kayittipi,@KISAKOD,@Firma)", conn);
                                    mysqlCommandLog.Parameters.AddWithValue("adi", name);
                                    mysqlCommandLog.Parameters.AddWithValue("soyadi", lastName);
                                    mysqlCommandLog.Parameters.AddWithValue("Telefon1", cepNo);
                                    mysqlCommandLog.Parameters.AddWithValue("kayittipi", 0);
                                    mysqlCommandLog.Parameters.AddWithValue("KISAKOD", code);
                                    mysqlCommandLog.Parameters.AddWithValue("Firma", $"{name}-{lastName} Personel");
                                    mysqlCommandLog.ExecuteNonQuery();

                                    if (!found && cepNo.NullToString() != "" && code.NullToString() != "")
                                    {
                                        logMessage = $"{name} {lastName} {code} {cepNo}";
                                        Console.WriteLine(logMessage);
                                        Logyaz(logMessage);
                                        var mysqlCommand = new MySqlCommand("INSERT INTO Rehber (adi,soyadi,Telefon1,kayittipi,KISAKOD,Firma) VALUE (@adi,@soyadi,@Telefon1,@kayittipi,@KISAKOD,@Firma)", conn);
                                        mysqlCommand.Parameters.AddWithValue("adi", name);
                                        mysqlCommand.Parameters.AddWithValue("soyadi", lastName);
                                        mysqlCommand.Parameters.AddWithValue("Telefon1", cepNo);
                                        mysqlCommand.Parameters.AddWithValue("kayittipi", 0);
                                        mysqlCommand.Parameters.AddWithValue("KISAKOD", code);
                                        mysqlCommand.Parameters.AddWithValue("Firma", $"{name}-{lastName} Personel");
                                        mysqlCommand.ExecuteNonQuery();
                                        var manager = new ManagerConnection(
                                            pbxIp,
                                            int.Parse(pbxPort),
                                            pbxUser,
                                            pbxPassword);
                                        manager.FireAllEvents = false;
                                        manager.Login();
                                        CommandAction commandAction = new CommandAction();
                                        var speedDialDelete = $" database del sysspeeddials {code}";
                                        var cidNameDelete = $" database del cidname {cepNo}";
                                        commandAction.Command = speedDialDelete;
                                        var response = manager.SendAction(commandAction);
                                        commandAction.Command = cidNameDelete;
                                        var response1 = manager.SendAction(commandAction);

                                        var speedDialAdd = $" database put sysspeeddials {code} {cepNo}";
                                        var cidNameAdd = $" database put cidname {cepNo} {name.Replace(" ", "-")}-{lastName.Replace(" ", "-")}";

                                        commandAction.Command = speedDialAdd;
                                        var response2 = manager.SendAction(commandAction);
                                        commandAction.Command = cidNameAdd;
                                        var response3 = manager.SendAction(commandAction);
                                        manager.Logoff();
                                    }

                                }
                                if (staff.Count() == 0)
                                    break;

                            }

                        }
                        RemoveStaleStudentContacts(conn, syncToken);
                        logMessage =
                            $"Toplam öğrenci-veli eşleşmesi: {studentContactCount}";
                        Console.WriteLine(logMessage);
                        Logyaz(logMessage);

                        SyncStudentAliasesToAsterisk(
                            pbxIp,
                            int.Parse(pbxPort),
                            pbxUser,
                            pbxPassword,
                            studentNamesByPhone,
                            speedDials);

                    }
                }
                logMessage = $"işlem tamamlandı";
                Console.WriteLine(logMessage);
                Logyaz(logMessage);
            }
            catch (Exception ex)
            {
                Logyaz(ex.Message);
            }

        }

        private static void Logyaz(string message)
        {
            var filePath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

            using (StreamWriter writer = new StreamWriter(filePath + "\\Log.txt", true))
            {
                writer.WriteLine($"{DateTime.Now} {message}");
            }

        }

        private static bool hasData(string studentId, string cepNo, MySqlConnection conn)
        {
            var result = false;
            using (var sqlcommand = new MySqlCommand("select * from Rehber where KISAKOD=@KISAKOD and Telefon1=@Telefon1", conn))
            {
                sqlcommand.Parameters.AddWithValue("KISAKOD", studentId);
                sqlcommand.Parameters.AddWithValue("Telefon1", cepNo);

                using (var cmd = sqlcommand.ExecuteReader())
                {
                    result = cmd.HasRows;
                }
            }
            return result;
        }

        private static void EnsureStudentContactSchema(MySqlConnection conn)
        {
            EnsureColumn(conn, "Rehber", "K12StudentID", "VARCHAR(36) NULL");
            EnsureColumn(conn, "Rehber", "K12ContactID", "VARCHAR(36) NULL");
            EnsureColumn(conn, "Rehber", "K12SyncToken", "VARCHAR(36) NULL");

            using (var command = new MySqlCommand(
                @"SELECT COUNT(*)
                    FROM information_schema.statistics
                   WHERE table_schema = DATABASE()
                     AND table_name = 'Rehber'
                     AND index_name = 'UX_Rehber_K12StudentContact'", conn))
            {
                if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                {
                    using (var createIndex = new MySqlCommand(
                        @"CREATE UNIQUE INDEX UX_Rehber_K12StudentContact
                            ON Rehber (K12StudentID, K12ContactID)", conn))
                    {
                        createIndex.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void EnsureColumn(
            MySqlConnection conn,
            string tableName,
            string columnName,
            string columnDefinition)
        {
            using (var command = new MySqlCommand(
                @"SELECT COUNT(*)
                    FROM information_schema.columns
                   WHERE table_schema = DATABASE()
                     AND table_name = @tableName
                     AND column_name = @columnName", conn))
            {
                command.Parameters.AddWithValue("@tableName", tableName);
                command.Parameters.AddWithValue("@columnName", columnName);

                if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                {
                    using (var alter = new MySqlCommand(
                        $"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {columnDefinition}", conn))
                    {
                        alter.ExecuteNonQuery();
                    }
                }
            }
        }

        private static void UpsertStudentContact(
            MySqlConnection conn,
            string studentName,
            string studentLastName,
            string phone,
            string code,
            Guid studentId,
            Guid contactId,
            string parentName,
            string syncToken)
        {
            using (var command = new MySqlCommand(
                @"INSERT INTO Rehber
                    (adi, soyadi, Telefon1, kayittipi, KISAKOD, Firma,
                     K12StudentID, K12ContactID, K12SyncToken)
                  VALUES
                    (@adi, @soyadi, @Telefon1, @kayittipi, @KISAKOD, @Firma,
                     @K12StudentID, @K12ContactID, @K12SyncToken)
                  ON DUPLICATE KEY UPDATE
                    adi = VALUES(adi),
                    soyadi = VALUES(soyadi),
                    Telefon1 = VALUES(Telefon1),
                    kayittipi = VALUES(kayittipi),
                    KISAKOD = VALUES(KISAKOD),
                    Firma = VALUES(Firma),
                    K12SyncToken = VALUES(K12SyncToken)", conn))
            {
                command.Parameters.AddWithValue("@adi", studentName);
                command.Parameters.AddWithValue("@soyadi", studentLastName);
                command.Parameters.AddWithValue("@Telefon1", phone);
                command.Parameters.AddWithValue("@kayittipi", 0);
                command.Parameters.AddWithValue("@KISAKOD", (object)code ?? DBNull.Value);
                command.Parameters.AddWithValue("@Firma", $"Veli: {parentName}");
                command.Parameters.AddWithValue("@K12StudentID", studentId.ToString());
                command.Parameters.AddWithValue("@K12ContactID", contactId.ToString());
                command.Parameters.AddWithValue("@K12SyncToken", syncToken);
                command.ExecuteNonQuery();
            }

            using (var logCommand = new MySqlCommand(
                @"INSERT INTO RehberLog
                    (adi, soyadi, Telefon1, kayittipi, KISAKOD, Firma)
                  VALUES
                    (@adi, @soyadi, @Telefon1, @kayittipi, @KISAKOD, @Firma)", conn))
            {
                logCommand.Parameters.AddWithValue("@adi", studentName);
                logCommand.Parameters.AddWithValue("@soyadi", studentLastName);
                logCommand.Parameters.AddWithValue("@Telefon1", phone);
                logCommand.Parameters.AddWithValue("@kayittipi", 0);
                logCommand.Parameters.AddWithValue("@KISAKOD", (object)code ?? DBNull.Value);
                logCommand.Parameters.AddWithValue("@Firma", $"Veli: {parentName}");
                logCommand.ExecuteNonQuery();
            }
        }

        private static void RemoveStaleStudentContacts(
            MySqlConnection conn,
            string syncToken)
        {
            using (var command = new MySqlCommand(
                @"DELETE FROM Rehber
                   WHERE K12StudentID IS NOT NULL
                     AND (K12SyncToken IS NULL OR K12SyncToken <> @K12SyncToken)", conn))
            {
                command.Parameters.AddWithValue("@K12SyncToken", syncToken);
                var deletedCount = command.ExecuteNonQuery();
                Logyaz($"Eski öğrenci-veli eşleşmesi silindi: {deletedCount}");
            }
        }

        private static void AddStudentPhoneAlias(
            Dictionary<string, HashSet<string>> studentNamesByPhone,
            string phone,
            string studentName)
        {
            HashSet<string> names;
            if (!studentNamesByPhone.TryGetValue(phone, out names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                studentNamesByPhone.Add(phone, names);
            }

            if (!string.IsNullOrWhiteSpace(studentName))
                names.Add(studentName);
        }

        private static void SyncStudentAliasesToAsterisk(
            string pbxIp,
            int pbxPort,
            string pbxUser,
            string pbxPassword,
            Dictionary<string, HashSet<string>> studentNamesByPhone,
            Dictionary<string, string> speedDials)
        {
            if (studentNamesByPhone.Count == 0 && speedDials.Count == 0)
                return;

            var manager = new ManagerConnection(
                pbxIp, pbxPort, pbxUser, pbxPassword);
            manager.FireAllEvents = false;

            try
            {
                manager.Login();
                manager.DefaultResponseTimeout = 10000;
                var completed = 0;
                var failed = 0;
                var cidNames = studentNamesByPhone.ToDictionary(
                    entry => entry.Key,
                    entry => SanitizeAsteriskValue(string.Join("-", entry.Value)));
                var total = speedDials.Count + cidNames.Count;

                foreach (var speedDial in speedDials)
                {
                    var command =
                        $"database put sysspeeddials {speedDial.Key} {speedDial.Value}";
                    string error;
                    if (!SendAsteriskCommand(manager, command, out error))
                    {
                        failed++;
                        Logyaz($"Asterisk hızlı arama hatası ({speedDial.Key}): {error}");
                    }

                    completed++;
                    LogAsteriskProgress(completed, total, failed);
                }

                foreach (var phoneEntry in cidNames)
                {
                    var command =
                        $"database put cidname {phoneEntry.Key} {phoneEntry.Value}";
                    string error;
                    if (!SendAsteriskCommand(manager, command, out error))
                    {
                        failed++;
                        Logyaz($"Asterisk cidname hatası ({phoneEntry.Key}): {error}");
                    }

                    completed++;
                    LogAsteriskProgress(completed, total, failed);
                }

                Logyaz($"Asterisk aktarımı tamamlandı. Toplam: {total}, Hata: {failed}");

                if (failed > 0)
                    throw new InvalidOperationException(
                        $"{failed} Asterisk kaydı üç denemeye rağmen yazılamadı.");

                VerifyAndRetryAsteriskFamily(
                    manager, "sysspeeddials", speedDials);
                VerifyAndRetryAsteriskFamily(
                    manager, "cidname", cidNames);
            }
            finally
            {
                if (manager.IsConnected())
                    manager.Logoff();
            }
        }

        private static void VerifyAndRetryAsteriskFamily(
            ManagerConnection manager,
            string family,
            Dictionary<string, string> expectedEntries)
        {
            const int retryRounds = 2;

            for (var round = 0; round <= retryRounds; round++)
            {
                var actualKeys = ReadAsteriskFamilyKeys(manager, family);
                var missingKeys = expectedEntries.Keys
                    .Where(key => !actualKeys.Contains(key))
                    .ToList();

                var message =
                    $"Asterisk doğrulama {family}: Beklenen={expectedEntries.Count}, " +
                    $"Bulunan={actualKeys.Count}, Eksik={missingKeys.Count}, Tur={round + 1}";
                Console.WriteLine(message);
                Logyaz(message);

                if (missingKeys.Count == 0)
                    return;

                if (round == retryRounds)
                {
                    Logyaz(
                        $"Asterisk {family} eksik örnekleri: " +
                        string.Join(",", missingKeys.Take(20)));
                    throw new InvalidOperationException(
                        $"Asterisk {family} doğrulaması başarısız. " +
                        $"{missingKeys.Count} kayıt eksik.");
                }

                var retried = 0;
                var retryFailed = 0;
                foreach (var key in missingKeys)
                {
                    string error;
                    var command =
                        $"database put {family} {key} {expectedEntries[key]}";
                    if (!SendAsteriskCommand(manager, command, out error))
                    {
                        retryFailed++;
                        Logyaz(
                            $"Asterisk {family} tekrar hatası ({key}): {error}");
                    }

                    retried++;
                    if (retried % 100 == 0 || retried == missingKeys.Count)
                    {
                        var retryMessage =
                            $"Asterisk {family} tekrar: " +
                            $"{retried}/{missingKeys.Count}, Hata={retryFailed}";
                        Console.WriteLine(retryMessage);
                        Logyaz(retryMessage);
                    }
                }
            }
        }

        private static HashSet<string> ReadAsteriskFamilyKeys(
            ManagerConnection manager,
            string family)
        {
            var action = new CommandAction
            {
                Command = $"database show {family}"
            };
            var response = manager.SendAction(action) as CommandResponse;

            if (response == null || response.Result == null)
            {
                throw new InvalidOperationException(
                    $"Asterisk {family} kayıtları okunamadı.");
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prefix = $"/{family}/";

            foreach (var line in response.Result)
            {
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var remainder = line.Substring(prefix.Length);
                var separatorIndex = remainder.IndexOf(" : ", StringComparison.Ordinal);
                var key = separatorIndex >= 0
                    ? remainder.Substring(0, separatorIndex)
                    : remainder.Split(new[] { ' ', '\t' },
                        StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(key))
                    result.Add(key.Trim());
            }

            return result;
        }

        private static bool SendAsteriskCommand(
            ManagerConnection manager,
            string command,
            out string error)
        {
            error = null;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    // Her gönderimde yeni ActionId üretilmesi için nesne tekrar kullanılmamalı.
                    var action = new CommandAction { Command = command };
                    var response = manager.SendAction(action);
                    var commandResponse = response as CommandResponse;
                    var result = commandResponse == null || commandResponse.Result == null
                        ? string.Empty
                        : string.Join(" ", commandResponse.Result);

                    var responseIsError = response == null ||
                        string.Equals(response.Response, "Error",
                            StringComparison.OrdinalIgnoreCase);
                    var resultIsError =
                        result.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        result.IndexOf("usage:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        result.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (!responseIsError && !resultIsError)
                        return true;

                    error = response == null
                        ? "Asterisk cevap vermedi."
                        : $"{response.Response} {response.Message} {result}".Trim();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                if (attempt < 3)
                    System.Threading.Thread.Sleep(250 * attempt);
            }

            return false;
        }

        private static void LogAsteriskProgress(int completed, int total, int failed)
        {
            if (completed == total || completed % 100 == 0)
            {
                var message =
                    $"Asterisk aktarımı: {completed}/{total}, Hata: {failed}";
                Console.WriteLine(message);
                Logyaz(message);
            }
        }

        private static string SanitizeAsteriskValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "BILINMIYOR";

            var sanitized = new string(value
                .Select(character =>
                    char.IsLetterOrDigit(character) || character == '-'
                        ? character
                        : '-')
                .ToArray());

            while (sanitized.Contains("--"))
                sanitized = sanitized.Replace("--", "-");

            return sanitized.Trim('-');
        }

        private static string SafeName(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.RemoveTurish();
        }
    }
}
