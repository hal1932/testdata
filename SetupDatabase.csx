#r "System.dll"
#r "System.Xml.Linq.dll"
#r "MySQL.Data.dll"
using System;
using System.IO;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using System.Xml.Linq;
using MySql.Data.MySqlClient;

var cResourceRoot = Path.GetFullPath("resources");

class Model
{
    public FileInfo File { get; private set; }
    public FileInfo Def { get; private set; }
    public FileInfo[] Textures { get; private set; } = new FileInfo[] { };

    public Model(string filepath)
    {
        File = new FileInfo(filepath);
        if (!File.Exists)
        {
            throw new InvalidOperationException("cannot find model file: " + filepath);
        }

        Def = File.Directory.EnumerateFiles("*.mdldef")
            .FirstOrDefault();
        if (Def == null || !Def.Exists)
        {
            throw new InvalidOperationException("cannot find modeldef file: " + filepath);
        }

        var xml = XElement.Load(Def.FullName);
        Textures = xml.Element("Textures")
            .Elements()
            .Select(xelem => new FileInfo(Path.Combine(Def.Directory.FullName, xelem.Element("Path").Value)))
            .ToArray();
    }
}

var models = Directory.EnumerateFiles(Path.Combine(cResourceRoot, "models"), "*.mdl", SearchOption.AllDirectories)
    .Select(path => new Model(path));

using (var conn = new MySqlConnection("server=localhost;uid=user;pwd=password;database=test"))
{
    conn.Open();

    NonQuery(conn, "START TRANSACTION");

    NonQuery(conn, "DROP TABLE IF EXISTS `model_texture_maps`");
    NonQuery(conn, "DROP TABLE IF EXISTS `models`");
    NonQuery(conn, "DROP TABLE IF EXISTS `textures`");

    NonQuery(conn, @"
CREATE TABLE `test`.`models` (
  `id` INT NOT NULL AUTO_INCREMENT,
  `name` VARCHAR(64) NOT NULL,
  `filepath` VARCHAR(128) NOT NULL,
  `created` DATETIME NOT NULL,
  `last_updated` DATETIME NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE INDEX `model_name_UNIQUE` (`name` ASC),
  UNIQUE INDEX `model_filepath_UNIQUE` (`filepath` ASC)
);");

    NonQuery(conn, @"
CREATE TABLE `test`.`textures` (
  `id` INT NOT NULL AUTO_INCREMENT,
  `name` VARCHAR(64) NOT NULL,
  `filepath` VARCHAR(128) NOT NULL,
  `created` DATETIME NOT NULL,
  `last_updated` DATETIME NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE INDEX `texture_name_UNIQUE` (`name` ASC),
  UNIQUE INDEX `texture_filepath_UNIQUE` (`filepath` ASC)
);");

    NonQuery(conn, @"
CREATE TABLE `model_texture_maps` (
  `model_id` int(11) NOT NULL,
  `texture_id` int(11) NOT NULL,
  KEY `model_id_idx` (`model_id`),
  KEY `texture_id_idx` (`texture_id`),
  CONSTRAINT `model_id` FOREIGN KEY (`model_id`) REFERENCES `models` (`id`) ON DELETE CASCADE ON UPDATE CASCADE,
  CONSTRAINT `texture_id` FOREIGN KEY (`texture_id`) REFERENCES `textures` (`id`) ON DELETE CASCADE ON UPDATE CASCADE
);");

    NonQuery(conn, "COMMIT");
}

//foreach (Model model in models)
Parallel.ForEach(models, (model) =>
{
    using (var conn = new MySqlConnection("server=localhost;uid=user;pwd=password;database=test"))
    {
        conn.Open();
        NonQuery(conn, "START TRANSACTION");

        Console.WriteLine(model.File.Name);

        var name = Path.GetFileNameWithoutExtension(model.File.Name);
        var filepath = CalcRelativePath(model.File.FullName, cResourceRoot);
        var created = CalcDateTimeStr(model.File.CreationTime);
        var lastUpdated = CalcDateTimeStr(model.File.LastWriteTime);

        var query = string.Format(
            @"INSERT INTO `models`" +
            @"  (`name`, `filepath`, `created`, `last_updated`)" +
            @"  values ('{0}', '{1}', '{2}', '{3}')",
            name, filepath, created, lastUpdated);
        NonQuery(conn, query);
        var modelId = QueryScalar(conn, "SELECT LAST_INSERT_ID()");

        foreach (var tex in model.Textures)
        {
            name = Path.GetFileNameWithoutExtension(tex.Name);
            filepath = CalcRelativePath(tex.FullName, cResourceRoot);
            created = CalcDateTimeStr(tex.CreationTime);
            lastUpdated = CalcDateTimeStr(tex.LastWriteTime);

            query = string.Format(
                @"INSERT IGNORE INTO `textures`" +
                @"  (`name`, `filepath`, `created`, `last_updated`)" +
                @"  values ('{0}', '{1}', '{2}', '{3}')",
                name, filepath, created, lastUpdated);

            var texId = (NonQuery(conn, query) == 1) ?
                QueryScalar(conn, "SELECT LAST_INSERT_ID()")
                : QueryScalar(conn, string.Format("select `id` from `textures` where `name` = '{0}'", name));

            query = string.Format(
                @"INSERT INTO `model_texture_maps`" +
                @"  (`model_id`, `texture_id`)" +
                @"  values ({0}, {1})",
                modelId, texId);
            NonQuery(conn, query);
        }

        NonQuery(conn, "COMMIT");
    }
});

List<Dictionary<string, object>> Query(MySqlConnection conn, string query)
{
    var adapter = new MySqlDataAdapter(query, conn);
    var data = new DataTable();
    adapter.Fill(data);

    var result = new List<Dictionary<string, object>>();
    foreach (DataRow row in data.Rows)
    {
        var item = new Dictionary<string, object>();
        foreach (DataColumn col in data.Columns)
        {
            item[col.ColumnName] = row[col];
        }
        result.Add(item);
    }
    return result;
}

object QueryScalar(MySqlConnection conn, string query)
{
    return new MySqlCommand(query, conn)
        .ExecuteScalar();
}

int NonQuery(MySqlConnection conn, string query)
{
    return new MySqlCommand(query, conn)
        .ExecuteNonQuery();
}

string CalcRelativePath(string path, string root)
{
    var relativeUrl = new Uri(root + Path.DirectorySeparatorChar)
        .MakeRelativeUri(new Uri(path));
    return relativeUrl.ToString();
}

string CalcDateTimeStr(DateTime value)
{
    return value.ToString("yyyy-MM-dd HH:mm:ss");
}
