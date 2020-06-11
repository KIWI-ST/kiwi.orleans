﻿using Customer.Entity;
using Establishment.EResponse;
using GrainImplement.CMS.Persistence;
using GrainImplement.CMS.Util;
using GrainInterface.CMS;
using Newtonsoft.Json;
using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GrainImplement.CMS.Service
{
    public class CMSService : Orleans.Grain, ICMS
    {

        readonly IPersistentState<CustomerManager> _customerManager;

        readonly IPersistentState<GroupManager> _groupManager;

        public CMSService(
            [PersistentState("CustomerManager", "CustomerManagerCache")] IPersistentState<CustomerManager> customerManager,
            [PersistentState("GroupManager", "GroupManagerCache")] IPersistentState<GroupManager> groupManager)
        {
            _customerManager = customerManager;
            _groupManager = groupManager;
        }

        /// <summary>
        /// 系统初始化校验
        /// </summary>
        /// <returns></returns>
        Task<bool> ICMS.InitialCheck()
        {
            try
            {
                _groupManager.ReadStateAsync();
                //内部初始化最高级管理组
                if (!_groupManager.State.GropuCollection.Any())
                {
                    _groupManager.State.GropuCollection = new List<Group>()
                {
                    new Group()
                    {
                        groupName = "系统管理员",
                        description = "系统自动初始化新建的管理员，具有最高权限",
                        level = 99
                    }
                };
                    _groupManager.WriteStateAsync();
                }
                _customerManager.ReadStateAsync();
                //内部初始化最高级管理员
                if (!_customerManager.State.CustomerCollection.Any())
                {
                    _customerManager.State.CustomerCollection = new List<User>()
                {
                    new User()
                    {
                        userName= "admin",
                        userPwd = "admin1",
                        groupObjectId = _groupManager.State.GropuCollection.Find(p=>p.level == 99)?.objectId
                    }
                };
                    _customerManager.WriteStateAsync();
                }
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// 创建group
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="token"></param>
        /// <param name="groupName"></param>
        /// <param name="groupDesc"></param>
        /// <param name="groupLevel"></param>
        /// <returns></returns>
        Task<string> ICMS.CreateGroup(string userName, string token, string groupName, string groupDesc, int groupLevel)
        {
            if(groupLevel>=Helper.LEVEL.CUSTOMLEVEL) return Task.FromResult(new FailResponse("无法创建高级权限群组").ToString());
            _groupManager.ReadStateAsync();
            Group result = _groupManager.State.GropuCollection.Find(p => p.groupName == groupName);
            if(result!=null) return Task.FromResult(new FailResponse("已存在同名用户组").ToString());
            int level = GetAccessLevel(userName, token).Result;
            if (level < Helper.LEVEL.ADMINLEVEL) return Task.FromResult(Helper.PermissionDeniedResponse);
            Group group = new Group()
            {
                groupName = groupName,
                description = groupDesc,
                level = groupLevel
            };
            _groupManager.State.GropuCollection.Add(group);
            _groupManager.WriteStateAsync();
            return Task.FromResult(new FailResponse("用户组创建成功").ToString());
        }

        /// <summary>
        /// 删除用户组
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="token"></param>
        /// <param name="groupObjectId"></param>
        /// <returns></returns>
        Task<string> ICMS.DeleteGroup(string userName, string token, string groupObjectId)
        {
            int level = GetAccessLevel(userName, token).Result;
            if (level < Helper.LEVEL.ADMINLEVEL) return Task.FromResult(Helper.PermissionDeniedResponse);
            Group result = _groupManager.State.GropuCollection.Find(p => p.objectId == groupObjectId);
            if(result == null)
                return Task.FromResult(new FailResponse("未找到用户组").ToString());
            else
                _groupManager.State.GropuCollection.Remove(result);
            _groupManager.WriteStateAsync();
            return Task.FromResult(new OkResponse("删除用户组成功").ToString());
        }

        /// <summary>
        /// 获取用户权限等级，如果是-1表示无任何特殊权限
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        public Task<int> GetAccessLevel(string userName, string token)
        {
            _customerManager.ReadStateAsync();
            User result = _customerManager.State.CustomerCollection.Find(p => string.Equals(p.userName, userName) && string.Equals(p.token, token));
            Group group = _groupManager.State.GropuCollection.Find(p => string.Equals(p.objectId, result?.groupObjectId));
            return Task.FromResult(group == null ? group.level : Helper.LEVEL.NOPERMISSIONLEVEL);
        }

        /// <summary>
        /// 获取用户组列表
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        Task<string> ICMS.GetGroupList(string userName, string token)
        {
            int level = GetAccessLevel(userName, token).Result;
            if (level < Helper.LEVEL.ADMINLEVEL) return Task.FromResult(Helper.PermissionDeniedResponse);
            var group = from g in _groupManager.State.GropuCollection
                          where g.level < Helper.LEVEL.ADMINLEVEL
                          select new {
                              g.objectId,
                              g.level,
                              g.groupName,
                              g.description,
                              g.date,
                              g.time,
                          };
            return Task.FromResult(new OkResponse(group).ToString());
        }

        Task<string> ICMS.Login(string raw)
        {
            User user = JsonConvert.DeserializeObject<User>(raw);
            //1. 检查字段是否合理
            if (!user.Verify()) return Task.FromResult(new FailResponse("用户名/密码不符合规则").ToString());
            //2. 检查、同步各分布式结点状态
            _customerManager.ReadStateAsync();
            User result = _customerManager.State.CustomerCollection.Find(p => string.Equals(p.userName, user.userName) && string.Equals(p.userPwd, user.userPwd));
            if (result != null)
            {
                //3. 分配guid
                result.token = Guid.NewGuid().ToString().Replace("-", "");
                //4. 查询所属group
                Group group = _groupManager.State.GropuCollection.Find(p => string.Equals(p.objectId, result.groupObjectId));
                //5. 同步结点状态
                _customerManager.WriteStateAsync();
                return Task.FromResult(new OkResponse(new
                {
                    result.userName,
                    result.token,
                    group?.groupName,
                    group?.level
                }).ToString());
            }
            return Task.FromResult(new FailResponse("用户/密码错误").ToString());
        }

        Task<string> ICMS.Register(string raw)
        {
            User user = JsonConvert.DeserializeObject<User>(raw);
            //1. 检查字段是否合理
            if (!user.Verify()) return Task.FromResult(new FailResponse("用户名/密码不符合规则").ToString());
            //2. 检查用户名是否已注册
            _customerManager.ReadStateAsync();
            User result = _customerManager.State.CustomerCollection.Find(p => string.Equals(p.userName, user.userName));
            if (result != null)
                return Task.FromResult(new FailResponse("用户已存在").ToString());
            else
                _customerManager.State.CustomerCollection.Add(user);
            //3.同步多端
            _customerManager.WriteStateAsync();
            return Task.FromResult(new OkResponse("用户注册成功").ToString());
        }

        /// <summary>
        /// 搜索包含关键词的用户
        /// </summary>
        /// <param name="searchWord"></param>
        /// <param name="userName"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        Task<string> ICMS.SearchCustomerByName(string searchWord, string userName, string token)
        {
            if(string.IsNullOrEmpty(searchWord) || string.IsNullOrWhiteSpace(searchWord))
                return Task.FromResult(new FailResponse("搜索关键词不合法").ToString());
            //判断权限是否满足要求
            int level = GetAccessLevel(userName, token).Result;
            if (level < Helper.LEVEL.ADMINLEVEL) return Task.FromResult(Helper.PermissionDeniedResponse);
            var users = from u in _customerManager.State.CustomerCollection
                        where u.userName.Contains(searchWord.Trim())
                        select new
                        {
                            u.objectId,
                            u.groupObjectId,
                            u.userName,
                            u.date,
                            u.time
                        };
            return Task.FromResult(new OkResponse(users).ToString());
        }

        /// <summary>
        /// 设置用户所属用户组
        /// </summary>
        /// <param name="userName"></param>
        /// <param name="token"></param>
        /// <param name="customerObjectId"></param>
        /// <param name="groupObjectId"></param>
        /// <returns></returns>
        Task<string> ICMS.SetCustomerGroup(string userName, string token, string customerObjectId, string groupObjectId)
        {
            if(string.IsNullOrEmpty(customerObjectId)||string.IsNullOrEmpty(groupObjectId))
                return Task.FromResult(new FailResponse("用户组或者用户ID不合法").ToString());
            //判断权限是否满足要求
            int level = GetAccessLevel(userName, token).Result;
            if (level < Helper.LEVEL.ADMINLEVEL) return Task.FromResult(Helper.PermissionDeniedResponse);
            _groupManager.ReadStateAsync();
            Group group = _groupManager.State.GropuCollection.Find(p => p.objectId.Equals(groupObjectId.Trim()));
            if(group == null) return Task.FromResult(new FailResponse("用户组不存在").ToString());
            User user = _customerManager.State.CustomerCollection.Find(p => p.objectId.Equals(customerObjectId.Trim()));
            user.groupObjectId = groupObjectId;
            _customerManager.WriteStateAsync();
            return Task.FromResult(new OkResponse("设置用户的组信息成功").ToString());
        }
    }
}