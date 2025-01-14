﻿using Microsoft.IdentityModel.Tokens;
using SpectruMineAPI.Models;
using SpectruMineAPI.Models.MojangResponses;
using SpectruMineAPI.Services.Database.CRUDs;
using SpectruMineAPI.Services.Hardcore;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpectruMineAPI.Services.Auth
{
    public class AuthService
    {
        private ICRUD<User> Users;
        private Mail.MailSenderService MailService;
        private ILogger Logger;
        public AuthService(UserCRUD users, Mail.MailSenderService mailService, ILogger<HardcoreService> logger)
        {
            Users = users;
            MailService = mailService;
            Logger = logger;
        }

        public async Task<User?> GetUserById(string id)
        {
            var user = await Users.GetAsync(x => x.Id == id);
            return user;
        }
        public async Task<Errors> CreateAccount(string Username, string Password, string Email)
        {
            var regexUsername = new Regex(@"^[a-zA-Z0-9_]{3,16}$");
            var regexPassword = new Regex(@"^(?=.*\d)(?=.*[a-z])(?=.*[A-Z]).{8,32}$");
            var regexMail = new Regex("^[a-zA-Z0-9.!#$%&’*+=?^_`{|}~-]+@[a-zA-Z0-9-]+(?:\\.[a-zA-Z0-9-]+)*$");
            string? uuid = null;
            //Секция проверки данных
            if (!regexUsername.IsMatch(Username) || !regexPassword.IsMatch(Password) || !regexMail.IsMatch(Email))
            {
                return Errors.RegexNotMatch;
            }
            if (AuthOptions.UseMojangChecks)
            {
                uuid = await AuthMojangAPI.GetUUIDFromMojang(Username);
                if (uuid == null)
                {
                    return Errors.UUIDFailed;
                }
            }
            // ФАЗА 1 - Удаление неактивированных аккаунтов
            var user = await Users.GetAsync(x => x._username == Username.ToLower() && !x.Verified);
            user = user ?? await Users.GetAsync(x => x.Email.ToLower() == Email.ToLower() && !x.Verified);
            if (user != null)
            {
                /* На случай если передумаем перезаписывать незарегавшегося пользователя
                foreach(var Code in user.MailCodes)
                {
                    if(Code.ExpireAt < DateTime.UtcNow)
                    {
                        user.MailCodes.Remove(Code);
                    }
                }
                if(user.MailCodes.Count > 0)
                {
                    return Errors.Conflict;
                }
                */
                await Users.DeleteAsync(user.Id);
            }//else ??????

            //ФАЗА 2 - Проверка существующих аккаунтов
            if (await Users.GetAsync(user => user.Email == Email && user.Verified) != null)
            {
                return Errors.MailRegistered;
            }
            if (await Users.GetAsync(user => user._username == Username.ToLower() && user.Verified) != null)
            {
                return Errors.Conflict;
            }
            //Создание аккаунта
            var code = Crypto.CalculateMD5(DateTime.UtcNow.ToString());
            if (AuthOptions.UseMail)
            {
                await Users.CreateAsync(new()
                {
                    Username = Username,
                    _username = Username.ToLower(),
                    Password = Crypto.CalculateSHA256(Password),
                    Email = Email,
                    UUID = uuid ?? Guid.NewGuid().ToString().Replace("-", ""),
                    MailCodes = new() {
                    new()
                    {
                        Code = code,
                        ExpireAt = DateTime.UtcNow.AddMinutes(5)
                    }
                }
                });
                //Отправка на почту кода регистрации
                MailService.SendMessageActivate(Email, code);
            }
            else
            {
                Logger.LogInformation("Activation code is: " + code);
                await Users.CreateAsync(new()
                {
                    Username = Username,
                    _username = Username.ToLower(),
                    Password = Crypto.CalculateSHA256(Password),
                    Email = Email,
                    UUID = uuid == null ? Guid.NewGuid().ToString() : uuid,
                    MailCodes = new() {new(){
                    Code = code,
                    ExpireAt = DateTime.UtcNow.AddMinutes(5)}},
                    Verified = false
                });
            }

            return Errors.Success;
        }
        public async Task<Errors> CheckUser(string Username, string Password)
        {
            var user = await Users.GetAsync(x => x._username == Username.ToLower());

            if (user == null)
            {
                user = await Users.GetAsync(x => x.Email == Username.ToLower());
                if (user == null)
                {
                    return Errors.UserNotFound;
                }
            }
            if (user.Password != Crypto.CalculateSHA256(Password)) return Errors.InvalidPassword;
            if (!user.Verified) return Errors.AccountDisabled;

            return Errors.Success;
        }

        public async Task<Errors> CheckToken(string refreshToken)
        {
            var userList = await Users.GetAsync();
            var user = userList.FirstOrDefault(x => x.RefreshTokens.FirstOrDefault(x => x.Token == refreshToken) != null);

            if (user == null) return Errors.UserNotFound;
            var Token = user.RefreshTokens.FirstOrDefault(x => x.Token == refreshToken);

            if (Token!.ExpireAt < DateTime.UtcNow)
            {
                user.RefreshTokens.Remove(Token);
                await Users.UpdateAsync(user.Id, user);
                return Errors.TokenExpire;
            }
            return Errors.Success;
        }

        /* TODO:
         * Добавить внутрь GenerateTokens и UpdateTokens новый метод проверяющий UUID пользователя 
         * и пытающийся изменить ник при его изменении. Если же ник проверить не удалось - оставить старый
         */
        public async Task<Tokens> GenerateTokens(string Username)
        {
            var user = await Users.GetAsync(x => x._username == Username.ToLower());
            if (user == null)
            {
                user = await Users.GetAsync(x => x.Email == Username.ToLower());
                if (user == null) throw new ArgumentNullException($"{nameof(user)} returned null");
            }
            //Username
            var username = await AuthMojangAPI.GetUsernameByUUID(user.UUID);
            if (username != null && username.ToLower() != user._username)
            {
                user.Username = username;
                user._username = username.ToLower();
            }
            var refreshToken = new RefreshToken()
            {
                Token = Crypto.CalculateSHA256(DateTime.UtcNow.ToString()),
                ExpireAt = DateTime.UtcNow.AddDays(30)
            };
            var accessToken = Crypto.GetAccessToken(user.Id);
            user.RefreshTokens.Add(refreshToken);
            await Users.UpdateAsync(user.Id, user);
            return new(accessToken, refreshToken);
        }

        public async Task<Tokens> UpdateTokens(string refreshToken)
        {
            var userList = await Users.GetAsync();
            var user = userList.FirstOrDefault(x => x.RefreshTokens.FirstOrDefault(x => x.Token == refreshToken) != null);
            if (user == null) throw new ArgumentNullException($"{nameof(user)} returned null");
            var username = await AuthMojangAPI.GetUsernameByUUID(user.UUID);
            //Username
            if (username != null && username.ToLower() != user._username)
            {
                user.Username = username;
                user._username = username.ToLower();
            }
            var newToken = new RefreshToken()
            {
                Token = Crypto.CalculateSHA256(DateTime.UtcNow.ToString()),
                ExpireAt = DateTime.UtcNow.AddDays(30)
            };
            var accessToken = Crypto.GetAccessToken(user.Id);
            user.RefreshTokens.Remove(user.RefreshTokens.FirstOrDefault(x => x.Token == refreshToken)!);
            user.RefreshTokens.Add(newToken);
            await Users.UpdateAsync(user.Id, user);
            return new(accessToken, newToken);
        }

        public async Task<Tokens> RemoveTokens(string refreshToken)
        {
            var userList = await Users.GetAsync();
            var user = userList.FirstOrDefault(x => x.RefreshTokens.FirstOrDefault(x => x.Token == refreshToken) != null);
            if (user == null) throw new ArgumentNullException($"{nameof(user)} returned null");
            user.RefreshTokens.Clear();
            var newToken = new RefreshToken()
            {
                Token = Crypto.CalculateSHA256(DateTime.UtcNow.ToString()),
                ExpireAt = DateTime.UtcNow.AddDays(30)
            };
            var accessToken = Crypto.GetAccessToken(user.Id);
            user.RefreshTokens.Remove(user.RefreshTokens.FirstOrDefault(x => x.Token == refreshToken)!);
            user.RefreshTokens.Add(newToken);
            await Users.UpdateAsync(user.Id, user);
            return new(accessToken, newToken);
        }

        public async void RemoveToken(string refreshToken)
        {
            var userList = await Users.GetAsync();
            var user = userList.FirstOrDefault(x => x.RefreshTokens.FirstOrDefault(x => x.Token == refreshToken) != null);
            if (user == null) throw new ArgumentNullException($"{nameof(user)} returned null");
            user.RefreshTokens.Remove(user.RefreshTokens.FirstOrDefault(x => x.Token == refreshToken)!);
            await Users.UpdateAsync(user.Id, user);
        }

        public async Task<Errors> UpdatePassword(string email, string password)
        {
            var regexMail = new Regex("^[a-zA-Z0-9.!#$%&’*+=?^_`{|}~-]+@[a-zA-Z0-9-]+(?:\\.[a-zA-Z0-9-]+)*$");
            var regexPassword = new Regex(@"^(?=.*\d)(?=.*[a-z])(?=.*[A-Z]).{8,32}$");
            var user = await Users.GetAsync(x => x.Email == email);
            if (user == null) return Errors.UserNotFound;
            if (!regexPassword.IsMatch(password) || !regexMail.IsMatch(email)) return Errors.RegexNotMatch;
            user.NewPassword = Crypto.CalculateSHA256(password);
            var code = Crypto.CalculateMD5(DateTime.UtcNow.ToString());
            user.MailCodes.Add(new()
            {
                Code = code,
                ExpireAt = DateTime.UtcNow.AddMinutes(5),
                isRestore = true
            });
            await Users.UpdateAsync(user.Id, user);
            MailService.SendMessageRestore(email, code);
            return Errors.Success;
        }

        public async Task<string?> GetMailById(string id)
        {
            var user = await Users.GetAsync(x => x.Id == id);//Предусматривается что тут username уже ToLower
            if (user == null) return null;
            return user.Email;
        }

        public async Task<List<User>> GetUsers()
        {
            return await Users.GetAsync();
        }

        public enum Errors { RegexNotMatch, Success, Conflict, UserNotFound, InvalidPassword, TokenExpire, AccountDisabled, UUIDFailed, MailRegistered }

        public record Tokens(string AccessToken, RefreshToken RefreshToken);
    }
    static class Crypto
    {
        public static string CalculateSHA256(string data)
        {
            return Convert.ToHexString(SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(data))).ToLower();
        }

        public static string CalculateMD5(string data)
        {
            return Convert.ToHexString(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(data))).ToLower();
        }

        public static string GetAccessToken(string id)
        {
            var claims = new List<Claim> { new Claim(ClaimTypes.Name, id) };
            var jwt = new JwtSecurityToken(
                    issuer: AuthOptions.ISSUER,
                    audience: AuthOptions.AUDIENCE,
                    claims: claims,
                    expires: DateTime.UtcNow, //Действует 5 минут
                    signingCredentials: new SigningCredentials(AuthOptions.GetSymmetricSecurityKey(), SecurityAlgorithms.HmacSha256));
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }
    }
}
