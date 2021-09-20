﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using System.Linq;
using DarienEngine;

/*
    This class represents core functionality for all units in the game; it must implement only the behavior that is common between
    the playable units (BaseUnit) and the NP-units (BaseUnitAI), i.e. movement/pathfinding, attack routine, etc.
*/
[RequireComponent(typeof(UnitAudioManager))]
public class RTSUnit : MonoBehaviour
{
    public class States
    {
        private States(string value) { Value = value; }

        public string Value { get; set; }

        public static States Moving { get { return new States("Moving"); } }
        public static States Parking { get { return new States("Parking"); } }
        public static States Standby { get { return new States("Standby"); } }
        public static States Ready { get { return new States("Ready"); } }
        public static States Conjuring { get { return new States("Conjuring"); } }
        public static States Attacking { get { return new States("Attacking"); } }
        public static States Guarding { get { return new States("Guarding"); } }
        public static States Patrolling { get { return new States("Patrolling"); } }
    }
    public States state { get; set; } = States.Standby;

    public Factions faction;
    public PlayerNumbers playerNumber = PlayerNumbers.Player1;
    public UnitCategories unitType;
    public Sprite unitIcon;
    public string unitName;

    // Health 
    public float maxHealth;
    private float currentHealth;
    public float health { get; set; } = 100;
    public float healthRechargeRate = 3.0f; // delay in seconds
    protected float nextHealthRecharge = 0;
    private float lastHitRechargeDelay = 5.0f;

    // Mana
    public int baseMana;
    public int mana { get; set; } = 0;
    protected float manaRechargeRate = 0.5f;
    protected float nextManaRecharge;
    public int manaIncome;
    public int manaStorage;
    public int buildCost;
    public float buildTime = 100;

    // Abilities
    public bool isKinematic = true; // Is the unit mobile
    public bool isBuilder = false;
    public bool canAttack = true;
    public float attackRate = 2.5f;
    public float attackRange = 1.0f;
    public float weaponDamage = 100;
    public bool phaseDie = false; // If this unit just disappears on die

    public bool isMeleeAttacker;
    public MeleeWeaponScript[] meleeWeapons;
    protected List<GameObject> enemiesInSight = new List<GameObject>();

    private float nextAttack = 0.0f;
    public bool nextAttackReady { get; set; } = false;
    public bool engagingTarget { get; set; } = false;
    protected bool isMovingToAttack = false;
    [HideInInspector] public bool isAttacking = false;
    [HideInInspector] public GameObject attackTarget;
    // @TODO: check StrongholdScript; ideally BaseUnit should handle the facing routine so these vars can remain protected
    public bool facing { get; set; } = false;
    public bool isDead { get; set; } = false;
    protected bool isParking = false;
    protected bool tryParkingDirection;
    protected States nextStateAfterParking;
    protected bool attackMove = false;

    // @TODO: implement commandQueue for multi-command type queueing (attackQueue, masterBuildQueue)
    protected Queue<CommandQueueItem> commandQueue = new Queue<CommandQueueItem>();
    protected CommandQueueItem currentCommand
    {
        get { return commandQueue.Peek(); }
        set
        {
            // Setting current command means resetting queue
            commandQueue.Clear();
            commandQueue.Enqueue(value);
        }
    }

    public Vector3 offset { get; set; } = new Vector3(1, 1, 1);
    protected RTSUnit super;

    // Re-use for HandleFacing
    private Vector3 faceDirection;
    private Quaternion targetRotation;

    // Misc
    protected int expPoints = 10;
    public float animStateTime { get; set; } = 0;
    private float avoidanceRadius;
    private float doubleAvoidanceRadius;
    private static float avoidanceTickerTo = 0.0f;
    private static float avoidanceTickerFrom = 0.0f;

    public ParticleSystem bloodSystem;

    // Components
    protected Player mainPlayer;
    protected NavMeshAgent _Agent;
    protected NavMeshObstacle _Obstacle;
    protected Animator _Animator;
    public UnitAudioManager AudioManager { get; set; }

    public UIManager.ActionMenuSettings.SpecialAttackItem[] specialAttacks;
    protected List<GameObject> whoCanSeeMe = new List<GameObject>();

    protected float dieTime = 30;

    private void Awake()
    {
        // Default move position, for units not instantiated with a parking location
        currentCommand = new CommandQueueItem { commandType = CommandTypes.Move, commandPoint = transform.position };
    }

    protected void Init()
    {
        // Mock "super" variable so BaseUnit can access this parent class
        super = this;

        // Current health starts as maxHealth
        currentHealth = maxHealth;

        // Initialize NavMesh objects
        if (isKinematic)
        {
            _Agent = GetComponent<NavMeshAgent>();
            _Obstacle = GetComponent<NavMeshObstacle>();
            _Obstacle.enabled = false;
            avoidanceRadius = _Agent.radius;
            doubleAvoidanceRadius = _Agent.radius * 2;
        }
        else
        {
            _Obstacle = GetComponent<NavMeshObstacle>();
        }

        // Get object offset values based on collider
        if (gameObject.GetComponent<BoxCollider>())
        {
            offset = gameObject.GetComponent<BoxCollider>().size;
        }
        else if (gameObject.GetComponent<CapsuleCollider>())
        {
            float r = gameObject.GetComponent<CapsuleCollider>().radius;
            offset = new Vector3(r, r, r);
        }

        // Unit selection behavior is attached to MainCamera
        mainPlayer = GameManager.Instance.PlayerMain.player;
        AudioManager = GetComponent<UnitAudioManager>();
        _Animator = GetComponent<Animator>();

        // Set up linkage with melee weapon(s) if unit is a melee attacker
        if (isMeleeAttacker && meleeWeapons.Length > 0)
            foreach (MeleeWeaponScript mw in meleeWeapons)
                mw.SetLinkage(this, weaponDamage);

        // Each unit on Start must group under appropriate player holder and add itself to virtual context
        Functions.AddUnitToPlayerContext(this);
    }

    protected float UpdateHealth()
    {
        // Slowly regenerate health automatically
        if (Time.time > nextHealthRecharge && health < 100)
        {
            // @TODO?: PlusHealth(healthIncrement);
            PlusHealth(1);
            nextHealthRecharge = Time.time + healthRechargeRate;
        }
        return health;
    }

    protected void HandleMovement()
    {
        if (_Agent.enabled)
        {
            if (commandQueue.Count > 0)
            {
                _Agent.SetDestination(currentCommand.commandPoint);

                // Parking should always be the first state of a new unit, even if it's just parking to transform.position
                if (isParking)
                {
                    isParking = !IsInRangeOf(currentCommand.commandPoint);
                    if (!isParking)
                    {
                        // Here we can reliably say last state was Parking and now parking is done, next state set here
                        state = nextStateAfterParking;
                    }
                }

                if (!IsInRangeOf(currentCommand.commandPoint))
                {
                    // Add extra steering while moving
                    if (IsMoving())
                        HandleFacing(_Agent.steeringTarget, 0.25f);
                }
                // If unit has reached the position, dequeue it
                else
                    commandQueue.Dequeue();

                // InflateAvoidanceRadius();
            }
            /* else
            {
                // Return to being an obstacle while not moving
                TryToggleToObstacle();
            } */
        }
    }

    protected void InflateAvoidanceRadius()
    {
        if (!IsInRangeOf(currentCommand.commandPoint, 4))
        {
            // Increase the avoidance radius while on the move
            if (_Agent.radius != doubleAvoidanceRadius)
            {
                // Debug.Log("Increasing avoidance radius " + _Agent.radius);
                _Agent.radius = Mathf.Lerp(_Agent.radius, doubleAvoidanceRadius, avoidanceTickerTo);
                avoidanceTickerTo += 0.05f * Time.deltaTime;
            }
            avoidanceTickerFrom = 0;
        }
        else
        {
            // Set avoidance radius back to normal when close to the target
            if (_Agent.radius != avoidanceRadius)
            {
                // Debug.Log("Decreasing avoidance radius " + _Agent.radius);
                _Agent.radius = Mathf.Lerp(_Agent.radius, avoidanceRadius, avoidanceTickerFrom);
                avoidanceTickerFrom += 0.25f * Time.deltaTime;
            }
            avoidanceTickerTo = 0;
        }
    }

    protected void HandleAttackRoutine()
    {
        // @TODO: handle "out of range" event for non-kinematic attackers

        // State isAttacking when engagingTarget and not still orienting to attack position
        if (engagingTarget && (isKinematic ? !isMovingToAttack : !facing))
            isAttacking = true;

        float rangeOffset = attackRange;
        // Melee attackers use a portion of the attackTarget's collider (offset) size
        if (attackTarget)
            rangeOffset = isMeleeAttacker ? attackTarget.GetComponent<RTSUnit>().offset.x * 0.85f : attackRange;

        // While locked on target but not in range, keep moving to attack position
        if (attackTarget && !IsInRangeOf(attackTarget.transform.position, rangeOffset))
        {
            TryToggleToAgent();

            isMovingToAttack = true;
            isAttacking = false;

            // Update or create command queue item for attacking target
            if (commandQueue.Count > 0)
            {
                // Update the Vector3 position for current command with current attackTarget position
                currentCommand.commandPoint = attackTarget.transform.position;
            }
            else
            {
                commandQueue.Enqueue(new CommandQueueItem
                {
                    commandType = CommandTypes.Attack,
                    commandPoint = attackTarget.transform.position,
                    attackInfo = new AttackInfo
                    {
                        attackTarget = attackTarget
                    }
                });
            }

        }
        // Once unit is in range, can stop moving
        else if (isMovingToAttack && attackTarget && IsInRangeOf(attackTarget.transform.position, rangeOffset))
        {
            Debug.Log("Arrived at target");
            TryToggleToObstacle();
            isMovingToAttack = false;
        }

        // @TODO: when done attacking/killed attackTarget, would be good to start like a "disperse" process 
        // so unit groups don't pack together or just stop in place when done attacking, but instead spread out 
        // a bit into a loose formation

        // Handle attacking behavior
        if (isAttacking && attackTarget)
        {
            // Kinematic units do facing; @Note: stationary attack units do their own facing routine (e.g. Stronghold)
            if (isKinematic && !IsMoving())
                facing = HandleFacing(attackTarget.transform.position, 0.5f); // Continue facing attackTarget while isAttacking

            // Attack interval
            if (Time.time > nextAttack)
            {
                nextAttack = Time.time + attackRate;
                nextAttackReady = true;
                if (AudioManager.unitPlaysAttackSounds)
                    AudioManager.PlayAttackSound();
            }
            else
                nextAttackReady = false;
        }
    }

    protected bool HandleFacing(Vector3 targetPosition, float threshold = 0.01f)
    {
        faceDirection = targetPosition - transform.position;
        faceDirection.y = 0; // This makes rotation only apply to y axis
        targetRotation = Quaternion.LookRotation(faceDirection);
        if (Quaternion.Angle(transform.rotation, targetRotation) <= threshold)
            return false;
        transform.rotation = Quaternion.Lerp(transform.rotation, targetRotation, 10.0f * Time.deltaTime);
        return true;
    }

    protected void HandleBumping(RTSUnit bumpedUnit)
    {
        // Debug.Log(unitName + ": Hello, " + bumpedUnit.unitName + ".");
        // If I am parking and bumped into someone who is not moving, adjust my moveToPosition with offset
        if (isParking && !bumpedUnit.IsMoving())
        {
            // @TODO: maybe like bumpedUnit.TryToggleToAgentWithTimer
            // set up so like if units bump each other, they temporarily are set back to NavMeshAgents to allow 
            // the bumping unit through, say a large formation the agent can't discern as one large obstacle to avoid

            Debug.Log(unitName + ": Whoops, sorry I bumped you while parking. I'll adjust my destination.");

            // If I bumped, dump the move position that led me here, then modify that position with offset then queue it back 
            Vector3 lastMove = commandQueue.Dequeue().commandPoint;
            lastMove.x += tryParkingDirection ? offset.x : -offset.x;
            commandQueue.Enqueue(new CommandQueueItem { commandType = CommandTypes.Move, commandPoint = lastMove });
        }
    }

    protected GameObject AutoPickAttackTarget()
    {
        // @TODO: need to account for attacking intangible units 

        // If current attackTarget has died, clear attack
        if (attackTarget && attackTarget.GetComponent<RTSUnit>() && attackTarget.GetComponent<RTSUnit>().isDead)
        {
            ClearAttack();

            // Toggle kinematic attacker back to movable agent
            TryToggleToAgent();

            // Stop any movement at this point until another valid target is picked
            // @TODO: to stop movement/commands should be able to just clear commandQueue
            // @TODO: stop unless there's another in the attackQueue
            commandQueue.Clear();
        }

        // Find closest target
        GameObject target = null;
        target = FindClosestEnemy();
        RTSUnit targetUnitScript = target ? target.GetComponent<RTSUnit>() : null;
        // If a valid target exists but attackTarget has not yet been assigned, lock on to this target
        if (target && targetUnitScript && !attackTarget)
            TryAttack(target);

        // @TODO: if attackTarget becomes null, move back to original location unless there's another unit around to attack
        // @TODO: if isAttacking/taking damage and health is low, AI start a retreat, player units can be set to have an auto retreat or retreat button

        return target;
    }

    private void OnTriggerEnter(Collider col)
    {
        string compareTag = gameObject.tag == "Enemy" ? "Friendly" : "Enemy";
        // Layer 11 is "Inner Trigger" layer, by its nature, a child of "Unit" layer
        if (col.isTrigger && col.gameObject.layer == 11 && col.gameObject.GetComponentInParent<RTSUnit>())
            HandleBumping(col.gameObject.GetComponentInParent<RTSUnit>()); // If collided object is in the "Inner Trigger" layer, we can pretty safely assume it's parent must be an RTSUnit
        // Layer 9 is "Unit" layer
        else if (col.gameObject.tag == compareTag && col.gameObject.layer == 9 && !col.isTrigger)
            enemiesInSight.Add(col.gameObject);
        // Layer 15 is "Fog of War" mask layer
        else if (col.gameObject.tag == compareTag && col.gameObject.layer == 15)
            whoCanSeeMe.Add(col.transform.parent.gameObject);
    }

    private void OnTriggerExit(Collider col)
    {
        string compareTag = gameObject.tag == "Enemy" ? "Friendly" : "Enemy";
        if (col.gameObject.tag == compareTag && col.gameObject.layer == 9 && !col.isTrigger)
            enemiesInSight.Remove(col.gameObject);
        else if (col.gameObject.tag == compareTag && col.gameObject.layer == 15)
            whoCanSeeMe.Remove(col.transform.parent.gameObject);
    }

    protected GameObject FindClosestEnemy()
    {
        GameObject closest = null;
        float distance = Mathf.Infinity;
        Vector3 position = transform.position;
        if (enemiesInSight.Count > 0)
            enemiesInSight = enemiesInSight.Where(item => item != null && !item.GetComponent<RTSUnit>().isDead).ToList();
        foreach (GameObject go in enemiesInSight)
        {
            Vector3 diff = go.transform.position - position;
            float curDistance = diff.sqrMagnitude;
            if (curDistance < distance)
            {
                closest = go;
                distance = curDistance;
            }
        }
        return closest;
    }

    protected void DebugNavPath()
    {
        var path = _Agent.path;
        for (int i = 0; i < path.corners.Length - 1; i++)
        {
            Debug.DrawLine(path.corners[i], path.corners[i + 1], Color.red);
        }
    }

    public void SetMove(Vector3 position, bool addToQueue = false, bool doAttackMove = false)
    {
        // If not holding shift on this move, clear the moveto queue and make this position first in queue
        if (!addToQueue)
            commandQueue.Clear();
        commandQueue.Enqueue(new CommandQueueItem { commandType = CommandTypes.Move, commandPoint = position });
        attackMove = doAttackMove;
        isParking = false;
        ClearAttack();
        state = States.Moving;
        TryToggleToAgent();
    }

    public void SetParking(Vector3 position)
    {
        isParking = true;
        state = States.Parking;
        currentCommand = new CommandQueueItem { commandType = CommandTypes.Move, commandPoint = position };
    }

    public void SetParking(Vector3 position, bool parkingDirectionToggle)
    {
        isParking = true;
        state = States.Parking;
        currentCommand = new CommandQueueItem { commandType = CommandTypes.Move, commandPoint = position };
        tryParkingDirection = parkingDirectionToggle;
    }

    public void Begin(DarienEngine.Directions facingDir, Vector3 parkPosition, bool parkToggle, States nextState)
    {
        // SetFacingDir(facingDir);
        // @TODO: if no parking/start is not parking
        SetParking(parkPosition, parkToggle);
        // @TODO next state after parking
        // @TODO: AI after park is done, SetPatrolPoints(parkPosition), state = nextState;
        nextStateAfterParking = nextState;
    }

    public void TryAttack(GameObject target, bool addToQueue = false)
    {
        if (canAttack)
        {
            Debug.Log(gameObject.name + " trying attack on " + target.name);
            attackTarget = target;
            engagingTarget = true;
            isMovingToAttack = true;
            TryToggleToAgent();

            // @TODO: handle queue of attack targets
            if (!addToQueue)
                commandQueue.Clear();
            commandQueue.Enqueue(new CommandQueueItem
            {
                commandType = CommandTypes.Attack,
                commandPoint = attackTarget.transform.position,
                attackInfo = new AttackInfo
                {
                    attackTarget = attackTarget
                }
            });

            state = States.Attacking;
        }
    }

    public void ClearAttack()
    {
        nextAttackReady = false;
        isAttacking = false;
        engagingTarget = false;
        attackTarget = null;
    }

    public void PlusHealth(int amount)
    {
        health += amount;
    }

    public void ReceiveDamage(float amount)
    {
        if (health > 0)
        {
            currentHealth -= amount;
            health = currentHealth * (100 / maxHealth);

            // Play the blood particle system for units with blood
            if (bloodSystem)
                bloodSystem.Play();
        }
        // Delay the health recharge after receiving damage
        nextHealthRecharge = Time.time + lastHitRechargeDelay;
    }

    protected void HandleDie()
    {
        isDead = true;
        ClearAttack();

        if (_Agent)
            _Agent.enabled = false;

        if (_Obstacle)
            _Obstacle.enabled = false;

        AudioManager.PlayDieSound();

        // @TODO: play animation, play the dust particle system, destroy this object, and finally instantiate corpse object
        // @TODO: some units don't have a die animation, e.g. Factory units like Barracks
        // also walls don't even have an animator component
        if (_Animator)
            _Animator.SetTrigger("die");

        // Remove this unit from the player context
        Functions.RemoveUnitFromPlayerContext(this);

        // @TODO: need to figure out how to do the white ghost die thing
        if (phaseDie)
            dieTime = 0; // Remove immediately 
    }

    public void TryToggleToAgent()
    {
        if (isKinematic && !_Agent.enabled)
        {
            _Obstacle.enabled = false;
            _Agent.enabled = true;
        }
    }

    public void TryToggleToObstacle()
    {
        if (isKinematic && !_Obstacle.enabled)
        {
            _Agent.enabled = false;
            _Obstacle.enabled = true;
        }
    }

    public Vector3 GetVelocity()
    {
        return _Agent.velocity;
    }

    public bool IsMoving()
    {
        return isKinematic && _Agent.enabled && (_Agent.velocity - Vector3.zero).sqrMagnitude > 0.15f;
    }

    public bool IsAttacking()
    {
        return canAttack && isAttacking;
    }

    public bool IsInRangeOf(Vector3 pos)
    {
        return (transform.position - pos).sqrMagnitude < Mathf.Pow(_Agent.stoppingDistance, 2);
    }

    public bool IsInRangeOf(Vector3 pos, float rng)
    {
        return (transform.position - pos).sqrMagnitude < Mathf.Pow(rng, 2);
    }
}
