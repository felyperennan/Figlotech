﻿using Figlotech.BDados;
using System;
using System.IO;

namespace imgdupt
{
    class Program
    {

        static void Usage() {
            FTH.WriteLine("Usage: imgdupt <directory_path> <options>");
            FTH.WriteLine("Options:");
            FTH.WriteLine("\t-sz <size:int> :");
            FTH.WriteLine("\t\tSets the precision, higher value means more accurate but slower comparison");
            FTH.WriteLine("\t-mindiff <mindiff:int> :");
            FTH.WriteLine("\t\tSets the minimum difference between images (in %) for the comparator to consider them different");
            FTH.WriteLine("\t-tol <tolerance:int> :");
            FTH.WriteLine("\t\tSets the pixel tolerance, low values may cause the comparator to see 2 apparently equal images as different because of differing artifact/quality");
            FTH.WriteLine("");
            FTH.WriteLine("The comparator is able to read any GDI+ compliant format and even compare images of different extensions, it will always give preference for larger images when finding duplicates.");
            return;
        }

        static void Main(string[] args) {
            FTH.WriteLine("------------------------------------------------");
            FTH.WriteLine("-- Figlotech Tools");
            FTH.WriteLine("-- imgdupt");
            FTH.WriteLine("-- Scans a folder and separates duplicate images");
            FTH.WriteLine("------------------------------------------------");
            if (args.Length == 1) {
                Usage();
                return;
            }

            ImageComparator comparator = new ImageComparator(args[0]);
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "-sz") {
                    try {
                        int size = Convert.ToInt32(args[i + 1]);
                        comparator.ComparatorSize = size * 2;
                    } catch (Exception) {
                        Usage();
                        return;
                    }
                }
                if (args[i] == "-tol") {
                    try {
                        int size = Convert.ToInt32(args[i + 1]);
                        comparator.Tollerance = size * 2;
                    } catch (Exception) {
                        Usage();
                        return;
                    }
                }
            }
            if(!Directory.Exists(args[0])) {
                FTH.WriteLine($"Directory does not exist {args[0]}");
                return;
            }

            comparator.RunDuplicateTester();
        }
    }
}