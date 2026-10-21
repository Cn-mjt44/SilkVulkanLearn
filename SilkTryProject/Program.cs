﻿using Silk.NET.Windowing;
using SilkTry;

Game_Vulkan app = new Game_Vulkan("SilkTry", true);

try
{
	app.Window.Run();

}
catch (Exception ex)
{
	Console.WriteLine($"Error!\n{ex.Message}");
	throw;
}


