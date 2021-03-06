﻿using System;
using System.Collections.Generic;
using Akka.Actor;
using Akka.Event;

namespace Bookstore.Persistence
{
    public abstract class EntityActor<TEntity> : ReceiveActor, IWithUnboundedStash
        where TEntity : class
    {
        private readonly IActorRef _persistenceActor;
        private readonly Queue<Action<TEntity>> _pendingInvocations = new Queue<Action<TEntity>>();
        protected TEntity Entity { get; private set; }
        protected abstract Guid Id { get; }
        public IStash Stash { get; set; }
        protected ILoggingAdapter Log { get; }

        protected EntityActor()
        {
            _persistenceActor = Context.ActorOf(Props.Create(() => new PersistenceActor<TEntity>()));
            Log = Context.GetLogger();
        }

        private void Recovering()
        {
            Receive<RecoverySuccess<TEntity>>(success =>
            {
                Entity = success.Entity;
                Stash.UnstashAll();
                UnbecomeStacked();
            });
            Receive<RecoveryFailure>(failure =>
            {
                Stash.UnstashAll();
                UnbecomeStacked();
                OnRecoveryFailure(failure.Exception);
            });

            ReceiveAny(message => Stash.Stash());
        }

        private void Creating()
        {
            Receive<CreateSuccess<TEntity>>(success =>
            {
                Entity = success.Entity;
                Stash.UnstashAll();
                UnbecomeStacked();
                _pendingInvocations.Dequeue()(Entity);
            });
            Receive<CreateFailure>(failure =>
            {
                Entity = default(TEntity);
                Stash.UnstashAll();
                UnbecomeStacked();
                OnPersistFailure(failure.Exception);
                _pendingInvocations.Dequeue();
            });

            ReceiveAny(message => Stash.Stash());
        }

        private void Updating()
        {
            Receive<UpdateSuccess<TEntity>>(success =>
            {
                Entity = success.Entity;
                Stash.UnstashAll();
                UnbecomeStacked();
                _pendingInvocations.Dequeue()(Entity);
            });
            Receive<UpdateFailure>(failure =>
            {
                _persistenceActor.Tell(new Recover(Id), Sender);
                OnPersistFailure(failure.Exception);
                UnbecomeStacked();
                BecomeStacked(Recovering);
            });
            ReceiveAny(message => Stash.Stash());
        }

        private void Removing()
        {
            Receive<RemoveSuccess<TEntity>>(success =>
            {
                Entity = default(TEntity);
                Stash.UnstashAll();
                UnbecomeStacked();
                _pendingInvocations.Dequeue()(success.Entity);
            });
            Receive<RemoveFailure>(failure =>
            {
                _persistenceActor.Tell(new Recover(Id), Sender);
                OnRemoveFailure(failure.Exception);
                UnbecomeStacked();
                BecomeStacked(Recovering);
            });
        }

        protected override void PreStart()
        {
            _persistenceActor.Tell(new Recover(Id), Sender);
            BecomeStacked(Recovering);
        }

        protected void Persist(TEntity entity, Action<TEntity> handler)
        {
            _pendingInvocations.Enqueue(handler);

            if (Equals(Entity, default(TEntity)))
            {
                _persistenceActor.Tell(new Create<TEntity>(entity), Sender);
                BecomeStacked(Creating);
            }
            else
            {
                _persistenceActor.Tell(new Update<TEntity>(entity), Sender);
                BecomeStacked(Updating);
            }
        }

        protected void Remove(TEntity entity, Action<TEntity> handler)
        {
            _pendingInvocations.Enqueue(handler);
            if (!Equals(Entity, default(TEntity)))
            {
                _persistenceActor.Tell(new Remove<TEntity>(entity), Sender);
                BecomeStacked(Removing);
            }
        }

        protected virtual void OnPersistFailure(Exception cause)
        {
            Log.Error(cause, "Failed to persist entity type [{0}] for Id [{1}]", typeof(TEntity), Id);
        }

        protected virtual void OnRemoveFailure(Exception cause)
        {
            Log.Error(cause, "Failed to persist entity type [{0}] for Id [{1}]", typeof(TEntity), Id);
        }

        protected virtual void OnRecoveryFailure(Exception cause)
        {
            Log.Error(cause, "Failed to restore entity type [{0}] for Id [{1}]", typeof(TEntity), Id);
        }
    }
}