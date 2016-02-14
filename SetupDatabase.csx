#r "System.dll"
#r "System.Xml.Linq.dll"
#r "MySQL.Data.dll"
using System;
using System.IO;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MySql.Data.MySqlClient;

var cResourceRoot = Path.GetFullPath("resources");
var cShopCount = 100;
var cShopTypeCount = 5;
var cItemCount = 3000;
var cItemTypeCount = 10;
var cModelCountPerItem = new KeyValuePair<int, int>(0, 5);
var cItemCoutnPerShop = new KeyValuePair<int, int>(1, 20);
var cItemPrice = new KeyValuePair<int, int>(1, 1000);
var cItemPriceFactor = 10;

var rand = new Random(1234567890);
var lockObj = new object();


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

try
{
    Main(Environment.GetCommandLineArgs());
}
catch (Exception e)
{
    Console.WriteLine(e);
    Console.WriteLine(e.Message);
}

void Main(string[] args)
{
    ThreadPool.SetMinThreads(1, 1);
    ThreadPool.SetMaxThreads(16, 16);

    var models = Directory.EnumerateFiles(Path.Combine(cResourceRoot, "models"), "*.mdl", SearchOption.AllDirectories)
        .Select(path => new Model(path));

    // テーブル初期化
    using (var conn = OpenConnection())
    {
        NonQuery(conn, "START TRANSACTION");

        NonQuery(conn, "DROP TABLE IF EXISTS `model_texture_maps`");
        NonQuery(conn, "DROP TABLE IF EXISTS `item_model_maps`");
        NonQuery(conn, "DROP TABLE IF EXISTS `shop_item_maps`");
        NonQuery(conn, "DROP TABLE IF EXISTS `models`");
        NonQuery(conn, "DROP TABLE IF EXISTS `textures`");
        NonQuery(conn, "DROP TABLE IF EXISTS `items`");
        NonQuery(conn, "DROP TABLE IF EXISTS `shops`");

        NonQuery(conn, @"
CREATE TABLE `models` (
  `id` INT NOT NULL AUTO_INCREMENT,
  `name` VARCHAR(64) NOT NULL,
  `filepath` VARCHAR(128) NOT NULL,
  `created` DATETIME NOT NULL,
  `last_updated` DATETIME NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE INDEX `model_name_UNIQUE` (`name` ASC),
  UNIQUE INDEX `model_filepath_UNIQUE` (`filepath` ASC)
)");

        NonQuery(conn, @"
CREATE TABLE `textures` (
  `id` INT NOT NULL AUTO_INCREMENT,
  `name` VARCHAR(64) NOT NULL,
  `filepath` VARCHAR(128) NOT NULL,
  `created` DATETIME NOT NULL,
  `last_updated` DATETIME NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE INDEX `texture_name_UNIQUE` (`name` ASC),
  UNIQUE INDEX `texture_filepath_UNIQUE` (`filepath` ASC)
)");

        NonQuery(conn, @"
CREATE TABLE `items` (
  `id` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(32) NOT NULL,
  `type_id` int(11) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `items_type_id_idx` (`type_id`)
)");

        NonQuery(conn, @"
CREATE TABLE `shops` (
  `id` int(11) NOT NULL AUTO_INCREMENT,
  `name` varchar(32) NOT NULL,
  `type_id` int(11) NOT NULL,
  PRIMARY KEY (`id`),
  KEY `shops_type_id_idx` (`type_id`)
)");

        NonQuery(conn, @"
CREATE TABLE `model_texture_maps` (
  `model_id` int(11) NOT NULL,
  `texture_id` int(11) NOT NULL,
  KEY `model_texture_maps_model_id_idx` (`model_id`),
  KEY `model_texture_maps_texture_id_idx` (`texture_id`),
  CONSTRAINT `model_texture_maps_model_id` FOREIGN KEY (`model_id`) REFERENCES `models` (`id`) ON DELETE CASCADE ON UPDATE CASCADE,
  CONSTRAINT `model_texture_maps_texture_id` FOREIGN KEY (`texture_id`) REFERENCES `textures` (`id`) ON DELETE CASCADE ON UPDATE CASCADE
)");

        NonQuery(conn, @"
CREATE TABLE `item_model_maps` (
  `item_id` int(11) NOT NULL,
  `model_id` int(11) DEFAULT NULL,
  KEY `item_model_maps_item_id_idx` (`item_id`),
  KEY `item_model_maps_model_id_idx` (`model_id`),
  CONSTRAINT `item_model_maps_item_id` FOREIGN KEY (`item_id`) REFERENCES `items` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `item_model_maps_model_id` FOREIGN KEY (`model_id`) REFERENCES `models` (`id`) ON DELETE CASCADE ON UPDATE CASCADE
)");

        NonQuery(conn, @"
CREATE TABLE `shop_item_maps` (
  `shop_id` int(11) NOT NULL,
  `item_id` int(11) DEFAULT NULL,
  `price` int(11) NOT NULL,
  KEY `shop_item_maps_shop_id_idx` (`shop_id`),
  KEY `shop_item_maps_item_id_idx` (`item_id`),
  CONSTRAINT `shop_item_maps_item_id` FOREIGN KEY (`item_id`) REFERENCES `items` (`id`) ON DELETE CASCADE ON UPDATE CASCADE,
  CONSTRAINT `shop_item_maps_shop_id` FOREIGN KEY (`shop_id`) REFERENCES `shops` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
)");

        NonQuery(conn, "COMMIT");
    }

    // モデル、テクスチャ初期化
    var modelIds = new List<ulong>();
    var texIdDic = new Dictionary<string, ulong>();
    Parallel.ForEach(models, (model) =>
    {
        Console.WriteLine(model.File.Name);

        using (var conn = OpenConnection())
        {
            NonQuery(conn, "START TRANSACTION");

            // モデル
            ulong modelId;
            {
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

                modelId = (ulong)QueryScalar(conn, "SELECT LAST_INSERT_ID()");

                lock(lockObj)
                {
                    modelIds.Add(modelId);
                }
            }

            // テクスチャ
            foreach (var tex in model.Textures)
            {
                var name = Path.GetFileNameWithoutExtension(tex.Name);
                var filepath = CalcRelativePath(tex.FullName, cResourceRoot);
                var created = CalcDateTimeStr(tex.CreationTime);
                var lastUpdated = CalcDateTimeStr(tex.LastWriteTime);

                var query = string.Format(
                    @"INSERT IGNORE INTO `textures`" +
                    @"  (`name`, `filepath`, `created`, `last_updated`)" +
                    @"  values ('{0}', '{1}', '{2}', '{3}')",
                    name, filepath, created, lastUpdated);

                ulong texId;
                lock (lockObj)
                {
                    if (NonQuery(conn, query) == 0)
                    {
                        texId = texIdDic[name];
                    }
                    else
                    {
                        texId = (ulong)QueryScalar(conn, "SELECT LAST_INSERT_ID()");
                        texIdDic[name] = texId;
                    }
                }

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

    // アイテム初期化
    var itemIds = new List<ulong>();
    Parallel.For(0, cItemCount, (i) =>
    {
        Console.WriteLine("item" + i.ToString("D4"));

        using (var conn = OpenConnection())
        {
            NonQuery(conn, "START TRANSACTION");

            // アイテム
            ulong itemId;
            {
                var query = string.Format(
                    @"INSERT INTO `items`" +
                    @"  (`name`, `type_id`)" +
                    @"  values ('{0}', {1})",
                    "item" + i.ToString("D4"), rand.Next(cItemTypeCount));
                NonQuery(conn, query);

                itemId = (ulong)QueryScalar(conn, "SELECT LAST_INSERT_ID()");

                lock(lockObj)
                {
                    itemIds.Add(itemId);
                }
            }

            // アイテムを構成するモデル
            {
                var modelCount = rand.Next(cModelCountPerItem.Key, cModelCountPerItem.Value);
                ulong[] containdModelIds = Enumerable.Range(0, modelCount)
                    .Select(_ => modelIds[rand.Next(modelIds.Count)])
                    .ToArray();

                if (containdModelIds.Length > 0)
                {
                    var queryValues = containdModelIds.Select(modelId => string.Format(@"({0}, {1})", itemId, modelId));
                    var query =
                        @"INSERT INTO `item_model_maps`" +
                        @"  (`item_id`, `model_id`)" +
                        @"  values " + string.Join(",", queryValues);
                    NonQuery(conn, query);
                }
            }

            NonQuery(conn, "COMMIT");
        }
    });

    // ショップ初期化
    Parallel.For(0, cShopCount, (i) =>
    {
        Console.WriteLine("shop" + i.ToString("D4"));

        using (var conn = OpenConnection())
        {
            NonQuery(conn, "START TRANSACTION");

            // ショップ
            ulong shopId;
            {
                var query = string.Format(
                    @"INSERT INTO `shops`" +
                    @"  (`name`, `type_id`)" +
                    @"  values ('{0}', {1})",
                    "shop" + i.ToString("D4"), rand.Next(cShopTypeCount));
                NonQuery(conn, query);

                shopId = (ulong)QueryScalar(conn, "SELECT LAST_INSERT_ID()");
            }

            // ショップを構成するアイテム
            {
                var itemCount = rand.Next(cItemCoutnPerShop.Key, cItemCoutnPerShop.Value);
                var containedItemIds = Enumerable.Range(0, itemCount)
                    .Select(_ => itemIds[rand.Next(itemIds.Count)])
                    .ToArray();

                var queryValues = new List<string>();
                foreach (var itemId in containedItemIds)
                {
                    var price = rand.Next(cItemPrice.Key, cItemPrice.Value) * cItemPriceFactor;
                    queryValues.Add(string.Format(@"({0}, {1}, {2})", shopId, itemId, price));
                }
                var query =
                    @"INSERT INTO `shop_item_maps`" +
                    @"  (`shop_id`, `item_id`, `price`)" +
                    @"  values " + string.Join(",", queryValues);
                NonQuery(conn, query);
            }

            NonQuery(conn, "COMMIT");
        }
    });
}


//********************************************************//


MySqlConnection OpenConnection()
{
    lock (lockObj)
    {
        var conn = new MySqlConnection("server=localhost;uid=user;pwd=password;database=test");
        conn.Open();
        return conn;
    }
}

Dictionary<string, object>[] Query(MySqlConnection conn, string query)
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
    return result.ToArray();
}

object QueryScalar(MySqlConnection conn, string query)
{
    var command = new MySqlCommand(query, conn);
    command.CommandTimeout = 0;
    return command.ExecuteScalar();
}

int NonQuery(MySqlConnection conn, string query)
{
    var command = new MySqlCommand(query, conn);
    command.CommandTimeout = 0;
    return command.ExecuteNonQuery();
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
