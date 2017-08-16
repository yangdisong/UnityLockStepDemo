﻿using FrameWork;
using Protocol;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SyncSystem<T> : ViewSystemBase where T : PlayerCommandBase, new()
{
    public override void Init()
    {
        GlobalEvent.AddTypeEvent<SyncEntityMsg>(ReceviceSyncEntity);
        GlobalEvent.AddTypeEvent<DestroyEntityMsg>(ReceviceDestroyEntityMsg);
        GlobalEvent.AddTypeEvent<ChangeSingletonComponentMsg>(ReceviceChangeSingletonCompMsg);
        GlobalEvent.AddTypeEvent<StartSyncMsg>(ReceviceStartSyncMsg);
        GlobalEvent.AddTypeEvent<T>(ReceviceCommandMsg);
    }

    public override void Dispose()
    {
        GlobalEvent.RemoveTypeEvent<SyncEntityMsg>(ReceviceSyncEntity);
        GlobalEvent.RemoveTypeEvent<DestroyEntityMsg>(ReceviceDestroyEntityMsg);
        GlobalEvent.RemoveTypeEvent<ChangeSingletonComponentMsg>(ReceviceChangeSingletonCompMsg);
        GlobalEvent.RemoveTypeEvent<StartSyncMsg>(ReceviceStartSyncMsg);
        GlobalEvent.RemoveTypeEvent<T>(ReceviceCommandMsg);
    }

    #region 消息接收
    Deserializer deserializer = new Deserializer();

    void ReceviceStartSyncMsg(StartSyncMsg msg, params object[] objs)
    {
        Debug.Log("StartSyncMsg " + msg.frame);

        m_world.FrameCount = msg.frame;
        m_world.IsStart = true;

        WorldManager.IntervalTime = msg.intervalTime;
    }

    void ReceviceSyncEntity(SyncEntityMsg msg, params object[] objs)
    {
        Debug.Log("ReceviceSyncEntity");

        RecordComponent rc = m_world.GetSingletonComp<RecordComponent>();

        ServiceMessageInfo info = new ServiceMessageInfo();
        info.m_frame = msg.frame;
        info.m_type = MessageType.SyncEntity;
        info.m_msg = msg;

        rc.m_messageList.Add(info);

        Recalc(msg.frame);
    }

    void ReceviceDestroyEntityMsg(DestroyEntityMsg msg, params object[] objs)
    {
        Debug.Log("ReceviceDestroyEntityMsg");

        RecordComponent rc = m_world.GetSingletonComp<RecordComponent>();

        ServiceMessageInfo info = new ServiceMessageInfo();
        info.m_frame = msg.frame;
        info.m_type = MessageType.DestroyEntity;
        info.m_msg = msg;

        rc.m_messageList.Add(info);

        Recalc(msg.frame);
    }

    void ReceviceChangeSingletonCompMsg(ChangeSingletonComponentMsg msg,params object[] objs)
    {
        Debug.Log("ChangeSingletonCompMsg");

        RecordComponent rc = m_world.GetSingletonComp<RecordComponent>();

        ServiceMessageInfo info = new ServiceMessageInfo();
        info.m_frame = msg.frame;
        info.m_type = MessageType.ChangeSingletonComponent;
        info.m_msg = msg;

        rc.m_messageList.Add(info);

        Recalc(msg.frame);
    }

    void ReceviceCommandMsg(T cmd ,params object[] objs)
    {
        Debug.Log("ReceviceCommandMsg " + cmd.frame + " world " + m_world.FrameCount);
        PlayerCommandRecordComponent pcrc = m_world.GetEntity(cmd.id).GetComp<PlayerCommandRecordComponent>();

        //判断帧数
        if (m_world.FrameCount >= cmd.frame)
        {
            Debug.Log("重计算");
            PlayerCommandBase input = null;
            
            if (m_world.FrameCount == cmd.frame)
            {
                input = pcrc.m_forecastInput;
            }
            else
            {
                input = pcrc.GetInputCahae(cmd.frame);
            }

            //替换原来的记录
            pcrc.ReplaceCommand(cmd);

            //与本地预测做判断，如果不一致则需要重新演算
            if (!cmd.Equals(input))
            { 
                Recalc(cmd.frame);
            }
        }
        //存入缓存
        else
        {
            Debug.Log("存入缓存");
            pcrc.m_serverCache.Add(cmd);
        }
    }
    #endregion

    #region 消息发送

    #endregion

    #region 同步重新演算

    /// <summary>
    /// 从目标帧开始重新演算
    /// </summary>
    /// <param name="frameCount"></param>
    public void Recalc(int frameCount)
    {
        Debug.Log("Recalc " +frameCount + " current " + m_world.FrameCount);

        //回退到目标帧
        RevertToFrame(frameCount);

        for (int i = frameCount; i <= m_world.FrameCount; i++)
        {
            //重新读取操作
            LoadPlayerInput(i);

            //服务器数据改动
            ExecuteServiceMessage(i);

            //重新演算
            m_world.Recalc(m_world.IntervalTime);
        }

        ClearRecordInfo(m_world.FrameCount - 1);
    }

    #region 状态回滚

    /// <summary>
    /// 获取目标帧之后的所有记录(包括目标帧)
    /// </summary>
    /// <param name="frameCount"></param>
    /// <returns></returns>
    public List<RecordInfo> GetRecordInfoAfterFrame(int frameCount)
    {
        RecordComponent rc = m_world.GetSingletonComp<RecordComponent>();

        List<RecordInfo> list = new List<RecordInfo>();

        for (int i = 0; i < rc.m_recordList.Count; i++)
        {
            if (rc.m_recordList[i].frame >= frameCount)
            {
                list.Add(rc.m_recordList[i]);
            }
        }

        return list;
    }

    public RecordInfo GetRecordInfo(int frameCount)
    {
        RecordComponent rc = m_world.GetSingletonComp<RecordComponent>();

        List<RecordInfo> list = new List<RecordInfo>();

        for (int i = 0; i < rc.m_recordList.Count; i++)
        {
            if (rc.m_recordList[i].frame == frameCount)
            {
                return rc.m_recordList[i];
            }
        }

        throw new Exception("GetRecordInfo Exception not find record by "+ frameCount) ;
    }

    public void ClearRecordInfo(int frameCount)
    {
        RecordComponent rc = m_world.GetSingletonComp<RecordComponent>();
        for (int i = 0; i < rc.m_recordList.Count; i++)
        {
            if (rc.m_recordList[i].frame >= frameCount)
            {
                rc.m_recordList.RemoveAt(i);
                i--;
            }
        }
    }

    public void RevertToFrame(int frameCount)
    {
        List<RecordInfo> list = GetRecordInfoAfterFrame(frameCount);

        for (int i = 0; i < list.Count; i++)
        {
            for (int j = list[i].m_changeData.Count -1; j >=0; j--)
            {
                RevertByChangeRecordInfo(list[i].m_changeData[j]);
            }
        }
    }

    public void RevertByChangeRecordInfo(ChangeRecordInfo info)
    {
        switch (info.m_type)
        {
            case ChangeType.AddComp: RevertAddComp(info); break;
            case ChangeType.ChangeComp: RevertChangeComp(info); break;
            case ChangeType.RemoveComp: RevertRemoveComp(info); break;
            case ChangeType.CreateEntity: RevertCreateEntity(info); break;
            case ChangeType.DestroyEntity: RevertDestroyEntity(info); break;
        }
    }

    public void RevertAddComp(ChangeRecordInfo info)
    {
        EntityBase entity = m_world.GetEntity(info.m_EnityID);

        entity.RemoveComp(info.m_compName);
    }

    public void RevertChangeComp(ChangeRecordInfo info)
    {
        EntityBase entity = m_world.GetEntity(info.m_EnityID);

        entity.ChangeComp(info.m_compName, info.m_comp);
    }

    public void RevertRemoveComp(ChangeRecordInfo info)
    {
        EntityBase entity = m_world.GetEntity(info.m_EnityID);

        entity.AddComp(info.m_compName, info.m_comp);
    }

    public void RevertCreateEntity(ChangeRecordInfo info)
    {
        m_world.DestroyEntity(info.m_EnityID);
    }

    public void RevertDestroyEntity(ChangeRecordInfo info)
    {
        m_world.CreateEntity(info.m_EnityID);
    }

    #endregion

    #region 读取历史输入数据

    void LoadPlayerInput(int frameCount)
    {
        List<EntityBase> list = m_world.GetEntiyList(new string[] { "PlayerCommandRecordComponent", typeof(T).Name });

        for (int i = 0; i < list.Count; i++)
        {
            PlayerCommandRecordComponent pcrc = list[i].GetComp<PlayerCommandRecordComponent>();
            T cmd = (T)pcrc.GetInputCahae(frameCount);
            if (cmd != null)
            {
                list[i].ChangeComp(cmd);
            }
            else
            {
                Debug.Log("LoadPlayerInput faild frameCount:" + frameCount);
            }
        }
    }

    #endregion

    #region 读取并执行服务器数据

    void ExecuteServiceMessage(int frameCount)
    {
        List<ServiceMessageInfo> list = LoadMessage(frameCount);

        for (int i = 0; i < list.Count; i++)
        {
            ExecuteMessgae(list[i]);
        }
    }

    #region 读取服务器消息

    List<ServiceMessageInfo> LoadMessage(int frameCount)
    {
        List<ServiceMessageInfo> list = new List<ServiceMessageInfo>();
        RecordComponent rc = m_world.GetSingletonComp<RecordComponent>();

        for (int i = 0; i < rc.m_messageList.Count; i++)
        {
            if(frameCount == rc.m_messageList[i].m_frame)
            {
                list.Add(rc.m_messageList[i]);
            }
        }

        return list;
    }

    #endregion

    #region 执行服务器消息

    void ExecuteMessgae(ServiceMessageInfo info)
    {
        switch(info.m_type)
        {
            case MessageType.ChangeSingletonComponent:
                ExecuteChangeSingletonCompMsg((ChangeSingletonComponentMsg)info.m_msg);
                break;
            case MessageType.DestroyEntity:
                ExecuteDestroyEntityMsg((DestroyEntityMsg)info.m_msg);
                break;
            case MessageType.SyncEntity:
                ExecuteSyncEntity((SyncEntityMsg)info.m_msg);
                break;
        }
    }

    void ExecuteSyncEntity(SyncEntityMsg msg)
    {
        EntityBase entity;
        if (!m_world.GetEntityIsExist(msg.id))
        {
            entity = m_world.CreateEntity(msg.id);
        }
        else
        {
            entity = m_world.GetEntity(msg.id);
        }

        for (int i = 0; i < msg.infos.Count; i++)
        {
            //Debug.Log("msg.infos[i].m_compName " + msg.infos[i].m_compName);
            ComponentBase comp = (ComponentBase)deserializer.Deserialize(msg.infos[i].m_compName, msg.infos[i].content);

            if (entity.GetExistComp(msg.infos[i].m_compName))
            {
                entity.ChangeComp(msg.infos[i].m_compName, comp);
            }
            else
            {
                entity.AddComp(msg.infos[i].m_compName, comp);
            }
        }
    }

    void ExecuteDestroyEntityMsg(DestroyEntityMsg msg)
    {
        m_world.DestroyEntity(msg.id);
    }

    void ExecuteChangeSingletonCompMsg(ChangeSingletonComponentMsg msg)
    {
        SingletonComponent comp = (SingletonComponent)deserializer.Deserialize(msg.info.m_compName, msg.info.content);
        m_world.ChangeSingleComp(msg.info.m_compName, comp);
    }
    #endregion

    #endregion

    #endregion
}