using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace EmeraudeAdmin
{
    // Côté serveur : commandes déposées dans un fichier (sans passer par le jeu d'un admin)
    // et dons gardés pour les joueurs absents.
    //
    // Fichiers dans BepInEx/config/emeraude-admin/ :
    //   commands.txt        une commande par ligne, avec ou sans « adm: » ; lu toutes les 2 s puis vidé
    //   replies.log         la réponse à chaque commande de commands.txt
    //   pending-gives.tsv   dons à des joueurs absents, remis à leur prochaine apparition
    static partial class Server
    {
        const float InboxEvery = 2f;
        const char Tab = '\t';
        static float nextInbox;
        static string inboxDir;

        static string ServerName => L.T("server", "serveur");

        static string InboxDir
        {
            get
            {
                if (inboxDir == null)
                {
                    inboxDir = Path.Combine(Paths.ConfigPath, "emeraude-admin");
                    Directory.CreateDirectory(inboxDir);
                }
                return inboxDir;
            }
        }

        static string CommandsPath => Path.Combine(InboxDir, "commands.txt");
        static string RepliesPath => Path.Combine(InboxDir, "replies.log");
        static string PendingPath => Path.Combine(InboxDir, "pending-gives.tsv");

        static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // Sur un serveur dédié, l'expéditeur « serveur » n'a pas de personnage : god, ghost, fly
        // et damage doivent alors nommer un joueur.
        static bool FromServerFile(long sender) => ZNet.instance.IsDedicated() && sender == ZNet.GetUID();

        static void CheckInbox()
        {
            if (Time.time < nextInbox) return;
            nextInbox = Time.time + InboxEvery;
            if (!File.Exists(CommandsPath) || new FileInfo(CommandsPath).Length == 0) return;

            // Le fichier est renommé avant lecture : une ligne ajoutée pendant ce temps part dans un nouveau commands.txt.
            string taken = CommandsPath + ".running";
            string[] lines;
            try
            {
                File.Delete(taken);
                File.Move(CommandsPath, taken);
                lines = File.ReadAllLines(taken, Utf8);
                File.Delete(taken);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Plugin.Name}] cannot read {CommandsPath}: {e.Message}");
                return;
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith(Commands.Prefix, StringComparison.OrdinalIgnoreCase)) line = line.Substring(Commands.Prefix.Length);
                if (line.Length == 0 || line.StartsWith("#")) continue;
                Write("admin.log", $"{ServerName} : {line}");
                string reply;
                try
                {
                    reply = Run(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), ServerName, ZNet.GetUID());
                }
                catch (Exception e)
                {
                    reply = L.T("Error: ", "Erreur : ") + e.Message;
                }
                string indented = string.Join("\n", reply.Split('\n').Select(l => "    " + l));
                try
                {
                    File.AppendAllText(RepliesPath, $"{Now} | {line}\n{indented}\n", Utf8);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[{Plugin.Name}] cannot write {RepliesPath}: {e.Message}");
                }
                Debug.Log($"[{Plugin.Name}] {line} -> {reply}");
            }
        }

        // ---------------------------------------------------------------- dons en attente

        // Joueurs déjà venus sur le serveur : ceux qui ont un journal (<Joueur>_<SteamID>.log), nom nettoyé par Clean.
        static List<string> KnownPlayers() =>
            Directory.GetFiles(Dir, "*.log")
                .Select(f => Path.GetFileName(f))
                .Where(f => f != "admin.log" && f.Count(c => c == '.') == 1 && f.LastIndexOf('_') > 0)
                .Select(f => f.Substring(0, f.LastIndexOf('_')))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Joueur absent mais connu : nom exact, sinon début de nom sans ambiguïté.
        static string FindAbsent(string name, out string error)
        {
            var known = KnownPlayers();
            string target = Clean(name);
            var found = known.Where(n => n.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 0) found = known.Where(n => n.StartsWith(target, StringComparison.OrdinalIgnoreCase)).ToList();
            error = found.Count == 1 ? null
                : found.Count == 0 ? L.T($"Player \"{name}\" is not online and has never played on this server.", $"Joueur « {name} » ni en ligne ni jamais venu sur ce serveur.")
                : L.T($"\"{name}\" is ambiguous: ", $"« {name} » est ambigu : ") + string.Join(", ", found);
            return found.Count == 1 ? found[0] : null;
        }

        static void QueueGive(string player, string prefab, int amount, int quality, string admin)
        {
            File.AppendAllText(PendingPath, string.Join(Tab.ToString(), Now, player, prefab, amount, quality, admin) + "\n", Utf8);
        }

        // Ligne de pending-gives.tsv : date, joueur, prefab, quantité, qualité, admin
        static bool ParsePending(string line, out string[] fields) =>
            (fields = line.Split(Tab)).Length == 6 && int.TryParse(fields[3], out _) && int.TryParse(fields[4], out _);

        // À l'apparition d'un joueur : lui remet ses dons en attente, puis les retire du fichier.
        static void DeliverPending(long uid, ZNetPeer peer, string name, Vector3 pos)
        {
            if (!File.Exists(PendingPath)) return;
            string me = Clean(name);
            List<string> lines;
            try
            {
                lines = File.ReadAllLines(PendingPath, Utf8).Where(l => l.Length > 0).ToList();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Plugin.Name}] cannot read {PendingPath}: {e.Message}");
                return;
            }
            var mine = lines.Where(l => ParsePending(l, out var f) && f[1].Equals(me, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count == 0) return;
            try
            {
                // Retirés avant l'envoi : un don n'est jamais remis deux fois
                File.WriteAllLines(PendingPath, lines.Except(mine), Utf8);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Plugin.Name}] cannot write {PendingPath}: {e.Message}");
                return;
            }
            foreach (string line in mine)
            {
                ParsePending(line, out var f);
                int amount = int.Parse(f[3]), quality = int.Parse(f[4]);
                ZRoutedRpc.instance.InvokeRoutedRPC(uid, Net.Give, f[2], amount, quality);
                Journal(peer, name, pos, L.T($"receives from {f[5]} (waiting since {f[0]}): {amount} {f[2]}", $"reçoit de {f[5]} (en attente depuis le {f[0]}) : {amount} {f[2]}")
                    + (quality > 1 ? L.T($" (quality {quality})", $" (qualité {quality})") : ""));
            }
        }

        // adm:pending [clear <joueur>]
        static string Pending(string[] a)
        {
            var lines = File.Exists(PendingPath) ? File.ReadAllLines(PendingPath, Utf8).Where(l => l.Length > 0).ToList() : new List<string>();
            var parsed = lines.Select(l => (line: l, ok: ParsePending(l, out var f), f)).Where(t => t.ok).ToList();
            if (a.Length >= 3 && a[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                string target = Clean(a[2]);
                var removed = parsed.Where(t => t.f[1].Equals(target, StringComparison.OrdinalIgnoreCase)).Select(t => t.line).ToList();
                if (removed.Count == 0) return L.T($"Nothing waiting for \"{a[2]}\".", $"Rien en attente pour « {a[2]} ».");
                File.WriteAllLines(PendingPath, lines.Except(removed), Utf8);
                return L.T($"{removed.Count} pending give(s) for {a[2]} cancelled.", $"{removed.Count} don(s) en attente pour {a[2]} annulé(s).");
            }
            if (a.Length > 1) return L.T("Usage: adm:pending  or  adm:pending clear <player>", "Usage : adm:attente  ou  adm:attente clear <joueur>");
            if (parsed.Count == 0) return L.T("No pending give.", "Aucun don en attente.");
            return string.Join("\n", parsed.GroupBy(t => t.f[1], StringComparer.OrdinalIgnoreCase).Select(g =>
                g.Key + L.T(": ", " : ") + string.Join(", ", g.Select(t => $"{t.f[3]} {t.f[2]}" + (t.f[4] != "1" ? $"*{t.f[4]}" : "")))));
        }
    }
}
