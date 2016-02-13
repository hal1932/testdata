#r "System.dll"
#r "System.Xml.Linq.dll"
using System;
using System.IO;
using System.Collections.Generic;
using System.Xml.Linq;

var rootDir = CreateDirectory(Path.Combine("resources"));
var modelRootDir = CreateDirectory(Path.Combine(rootDir.FullName, "models"));

const int cModelCount = 1000;
const int cTextureCountPerModel = 5;
const int cCommonTextureCountPerModel = 3;

var modelDic = new Dictionary<FileInfo, List<FileInfo>>();

// モデル、個別テクスチャ
for (var i = 0; i < cModelCount; ++i)
{
    // mdl
    var name = "model" + i.ToString("D4");
    var dir = CreateDirectory(Path.Combine(modelRootDir.FullName, name));

    var modelfile = new FileInfo(Path.Combine(dir.FullName, name) + ".mdl");
    modelfile.OpenWrite()
        .Close();

    // tex
    var textures = new List<FileInfo>();
    var texdir = CreateDirectory(Path.Combine(dir.FullName, "textures"));
    for (var j = 0; j < cTextureCountPerModel; ++j)
    {
        var texname = name + "_" + j.ToString("D2") + ".tex";
        var texpath = Path.Combine(texdir.FullName, texname);
        var texfile = new FileInfo(texpath);
        texfile.OpenWrite()
            .Close();
        textures.Add(texfile);
    }
    modelDic[modelfile] = textures;
}

// 共通テクスチャ
var commonDir = Path.Combine(modelRootDir.FullName, "common");
var commonTexDir = CreateDirectory(Path.Combine(commonDir, "textures"));
var commonTextures = new List<FileInfo>();
for (var i = 0; i < 50; ++i)
{
    var tex = new FileInfo(Path.Combine(commonTexDir.FullName, "common" + i.ToString("D4") + ".tex"));
    tex.OpenWrite()
        .Close();
    commonTextures.Add(tex);
}
var rand = new Random(1234567890);
foreach (var item in modelDic)
{
    for (var i = 0; i < cCommonTextureCountPerModel; ++i)
    {
        var texIdx = rand.Next(commonTextures.Count);
        item.Value.Add(commonTextures[texIdx]);
    }
}

// モデル定義ファイル
foreach (var item in modelDic)
{
    var model = item.Key;
    var textures = item.Value;

    // mdldef
    var defPath = model.FullName.Replace(".mdl", ".mdldef");
    var defDir = model.Directory.FullName;

    var xml = new XElement("ModelDef",
        new XElement("Model",
            new XElement("Path", CalcRelativePath(model.FullName, defDir))),
        new XElement("Textures"));
    var texElem = xml.Element("Textures");
    foreach (var tex in textures)
    {
        texElem.Add(new XElement("Texture",
            new XElement("Path", CalcRelativePath(tex.FullName, defDir))));
    }

    xml.Save(defPath);
}

DirectoryInfo CreateDirectory(string path)
{
    path = Path.GetFullPath(path);
    var dir = new DirectoryInfo(path);
    if (dir.Exists)
    {
        Directory.Delete(dir.FullName, true);
    }
    Directory.CreateDirectory(dir.FullName);
    return dir;
}

string CalcRelativePath(string path, string root)
{
    var relativeUrl = new Uri(root + Path.DirectorySeparatorChar)
        .MakeRelativeUri(new Uri(path));
    return relativeUrl.ToString();
}
