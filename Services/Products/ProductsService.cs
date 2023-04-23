﻿using MongoDB.Bson;
using SpectruMineAPI.Models;
using SpectruMineAPI.Services.Database.CRUDs;
using System.Xml.Linq;

namespace SpectruMineAPI.Services.Products
{
    public class ProductsService
    {
        //TODO: Change CRUD to ICRUD (Add method with List<T> GetAsyncList
        readonly ProductsCRUD Products;
        readonly ICRUD<User> Users;
        public ProductsService(ProductsCRUD products, UserCRUD users)
        { 
            Products = products;
            Users = users;
        }
        public async Task<List<Product>> GetProductsAsync(string category)
        {
            var products = await Products.GetAsyncList(x => x.Category == category);
            return products;
        }
        public async Task CreateProduct()
        {
            await Products.CreateAsync(new Product()
            {
                Name = "Hat" + new Random(((int)DateTimeOffset.Now.ToUnixTimeSeconds())).Next(),
                Description = "HatDiscr",
                Category = "hardcore",
                ImgUrl = "/images/logo.png",
                ObjUrl = "/models/test_hat.obj",
                MatUrl = "/textures/tast_hat.mat",
                Price = 100
            });
        }
        public async Task CreateProduct(string Name, string Description, string Category, string ImgUrl, string ObjUrl, string MatUrl, float Price)
        {
            await Products.CreateAsync(new Product()
            {
                Name = Name,
                Description = Description,
                Category = Category,
                ImgUrl = ImgUrl,
                ObjUrl = ObjUrl,
                MatUrl = MatUrl,
                Price = Price
            });
        }
        public async Task<List<Product>?> GetInventoryById(string id)
        {
            var user = await Users.GetAsync(x => x.Id == id);
            if (user == null) return null;
            var products = new List<Product>();
            user.Inventory.ForEach(async y =>
            {
                var product = await Products.GetAsync(x => x.Id == y.ToString());
                if (product == null) return;
                products.Add(product);
            });
            return products;
        }
        public async Task<List<Product>?> GetInventoryByUsername(string username)
        {
            var user = await Users.GetAsync(x => x._username == username.ToLower());
            if (user == null) return null;
            return await GetInventoryById(user.Id);
        }
    }
}
