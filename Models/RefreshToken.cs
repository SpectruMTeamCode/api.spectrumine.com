﻿namespace SpectruMineAPI.Models
{
    public class RefreshToken
    {
        public string Token { get; set; } = null!;
        public DateTime ExpireAt { get; set; }
    }
}